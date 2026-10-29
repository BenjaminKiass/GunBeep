using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace GunBeep
{
    // สคริปต์หนึ่งตัวใน %LOCALAPPDATA%\LGHUB\scripts\<id>\script.lua
    public class ScriptSlot
    {
        public string Folder;
        public string Name;
        public string Text;
        public int Version;
        public bool IsGunBeep;
        public DateTime Modified;
    }

    public class GHubStatus
    {
        public bool Installed;
        public string Version;
        public bool ProfileFound;
        public bool Locked;
        public bool ProfileHasScript;
        public ScriptSlot Script;
        public int LatestVersion;
        public string Error;

        public bool ScriptUpToDate => Script != null && Script.Version >= LatestVersion;
        public bool AllGood => Installed && ProfileFound && Locked && ProfileHasScript && ScriptUpToDate;
    }

    // ตรวจ / ติดตั้ง / ใส่สคริปต์ / ล็อกโปรไฟล์ ของ Logitech G Hub
    public static class GHub
    {
        public const string ExePath = @"C:\Program Files\LGHUB\lghub.exe";

        static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LGHUB");
        static readonly string ScriptsDir = Path.Combine(Local, "scripts");
        static readonly string DbPath = Path.Combine(Local, "settings.db");
        static readonly string BackupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GunBeep", "backups");

        static readonly Regex VersionRx = new Regex(@"GunBeep-Script-Version:\s*(\d+)");
        // ส่วนตารางปืน (ตั้งแต่หัวข้อ "ปืน" ถึง local gun_idx) เก็บค่าที่ผู้ใช้จูนไว้ตอนอัปเดตสคริปต์
        static readonly Regex GunSection = new Regex(@"(?ms)^-- ===== ปืน.*?^local gun_idx = 1[ \t]*\r?$");

        // ---------------------------------------------------------------- สถานะ

        public static GHubStatus GetStatus()
        {
            var st = new GHubStatus { LatestVersion = ParseVersion(Template()) };
            try
            {
                st.Version = InstalledVersion();
                st.Installed = File.Exists(ExePath) || st.Version != null;
                if (!st.Installed) return st;

                st.Script = GHubPick(ListScripts());
                if (st.Script != null) SaveGunSection(st.Script);

                var json = ReadSettingsJson(DbPath, copyFirst: true, out _, out _);
                if (json != null)
                {
                    var prof = FindPubgProfile(json);
                    st.ProfileFound = prof != null;
                    if (prof != null)
                    {
                        string id = prof["id"]?.GetValue<string>();
                        st.Locked = json["persistent"]?["id"]?.GetValue<string>() == id;
                        st.ProfileHasScript = !string.IsNullOrEmpty(prof["scriptId"]?.GetValue<string>());
                    }
                }
            }
            catch (Exception ex) { st.Error = ex.Message; }
            return st;
        }

        static string InstalledVersion()
        {
            string[] roots =
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var root in roots)
                {
                    using var k = hive.OpenSubKey(root);
                    if (k == null) continue;
                    foreach (var name in k.GetSubKeyNames())
                    {
                        using var sub = k.OpenSubKey(name);
                        if ((sub?.GetValue("DisplayName") as string)?.Contains("G HUB") == true)
                            return sub.GetValue("DisplayVersion") as string ?? "?";
                    }
                }
            return null;
        }

        // ---------------------------------------------------------------- ติดตั้ง

        public static string FindInstaller()
        {
            var dirs = new List<string>
            {
                AppContext.BaseDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "มาโครพับจี"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            };
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                var hit = Directory.GetFiles(d, "*.exe")
                    .Where(f => Regex.IsMatch(Path.GetFileName(f), @"g-?hub|lghub", RegexOptions.IgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }

        // ลายเซ็นต้องถูกต้อง (ไฟล์ไม่ถูกแก้) และเซ็นโดย Logitech
        public static bool IsLogitechSigned(string path)
        {
            if (!WinTrust.Verify(path)) return false;
            try
            {
#pragma warning disable SYSLIB0057
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                return cert.Subject.Contains("O=Logitech");
            }
            catch { return false; }
        }

        public static async Task RunInstallerAsync(string path)
        {
            var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            if (p != null) await p.WaitForExitAsync();
        }

        // ---------------------------------------------------------------- เปิด/ปิด G Hub

        public static bool IsRunning =>
            Process.GetProcessesByName("lghub").Length > 0 || Process.GetProcessesByName("lghub_agent").Length > 0;

        public static async Task StopAsync()
        {
            foreach (var name in new[] { "lghub", "lghub_agent" })
                foreach (var p in Process.GetProcessesByName(name))
                    try { p.Kill(); } catch { }
            for (int i = 0; i < 40 && IsRunning; i++) await Task.Delay(250);
            await Task.Delay(500);
        }

        public static void StartBackground() =>
            Process.Start(new ProcessStartInfo(ExePath, "--background") { UseShellExecute = true });

        public static void OpenWindow() =>
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true });

        public static async Task<bool> WaitAgentAsync(int seconds)
        {
            for (int i = 0; i < seconds * 4; i++)
            {
                if (Process.GetProcessesByName("lghub_agent").Length > 0) return true;
                await Task.Delay(250);
            }
            return false;
        }

        // ---------------------------------------------------------------- สคริปต์

        public static string Template()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("recoil.lua");
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        static int ParseVersion(string text)
        {
            var m = VersionRx.Match(text ?? "");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        public static List<ScriptSlot> ListScripts()
        {
            var list = new List<ScriptSlot>();
            if (!Directory.Exists(ScriptsDir)) return list;
            foreach (var dir in Directory.GetDirectories(ScriptsDir))
            {
                string lua = Path.Combine(dir, "script.lua");
                if (!File.Exists(lua)) continue;
                string text = File.ReadAllText(lua, Encoding.UTF8);
                string name = Path.GetFileName(dir);
                try
                {
                    var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
                    name = cfg?["name"]?.GetValue<string>() ?? name;
                }
                catch { }
                list.Add(new ScriptSlot
                {
                    Folder = dir, Name = name, Text = text,
                    Version = ParseVersion(text), IsGunBeep = text.Contains("GunBeep"),
                    Modified = File.GetLastWriteTime(lua)
                });
            }
            return list;
        }

        // เลือกช่องสคริปต์ที่จะเขียน: ตัวที่ผูกกับโปรไฟล์ PUBG ใน settings.db ก่อน
        static string pubgScriptId;
        static DateTime pubgScriptRead;

        // ตัวขยับเมาส์ในตัว GunBeep (ไม่ใช้ G Hub): ค่าปืนเก็บใน saved\engine\script.lua รูปแบบเดียวกับสคริปต์ G Hub
        //   → โค้ดอ่าน/เขียน base, x, กราฟ, ตารางรายนัด ใช้ของเดิมได้ทั้งหมด
        //   ครั้งแรกสร้างจากตารางปืนในสคริปต์ G Hub (ถ้ามี) หรือ saved\guns.lua — ค่าที่จูนไว้ไม่หาย
        public static bool Native;
        static readonly string NativeDir = Path.Combine(Settings.KitDir, "engine");

        static ScriptSlot PickScript(List<ScriptSlot> list) => Native ? NativeSlot() : GHubPick(list);

        static ScriptSlot NativeSlot()
        {
            string lua = Path.Combine(NativeDir, "script.lua");
            if (!File.Exists(lua))
            {
                Directory.CreateDirectory(NativeDir);
                string text = Template();
                var gh = GHubPick(ListScripts());
                string guns = gh != null && gh.IsGunBeep ? GunSection.Match(gh.Text).Value : "";
                if (guns == "" && File.Exists(SavedGuns)) guns = File.ReadAllText(SavedGuns, Encoding.UTF8);
                var t = GunSection.Match(text);
                if (guns != "" && t.Success) text = text.Substring(0, t.Index) + guns + text.Substring(t.Index + t.Length);
                SafeFile.Write(lua, text);
                Log.Info("engine: created saved\\engine\\script.lua from " + (gh?.IsGunBeep == true ? "G Hub script" : guns != "" ? "saved\\guns.lua" : "template"));
            }
            string s = File.ReadAllText(lua, Encoding.UTF8);
            return new ScriptSlot
            {
                Folder = NativeDir, Name = "GunBeep (ในตัว)", Text = s, Version = ParseVersion(s), IsGunBeep = true,
                Modified = File.GetLastWriteTime(lua)
            };
        }

        static ScriptSlot GHubPick(List<ScriptSlot> list)
        {
            if (list == null || list.Count == 0) return null;
            try
            {
                if (DateTime.Now - pubgScriptRead > TimeSpan.FromSeconds(30))
                {
                    var json = ReadSettingsJson(DbPath, copyFirst: true, out _, out _);
                    var prof = json != null ? FindPubgProfile(json) : null;
                    pubgScriptId = prof?["scriptId"]?.GetValue<string>();
                    pubgScriptRead = DateTime.Now;
                }
                if (!string.IsNullOrEmpty(pubgScriptId))
                {
                    var match = list.FirstOrDefault(s => Path.GetFileName(s.Folder).Equals(pubgScriptId, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match;
                }
            }
            catch { }
            return list.Where(s => s.IsGunBeep).OrderByDescending(s => s.Modified).FirstOrDefault()
                ?? list.OrderByDescending(s => s.Modified).FirstOrDefault();
        }

        // ---------------------------------------------------------------- ค่า base / x ของแต่ละปืน

        static readonly Regex GunLine = new Regex(
            @"\{\s*name\s*=\s*""([^""]*)""\s*,\s*base\s*=\s*(-?[\d.]+)\s*,\s*x\s*=\s*(-?[\d.]+)");

        public class GunVals
        {
            public double Base, X;
            public double[] Steps;      // แรงดึงต่อช่วง 10 ms (ขยายจากตาราง segs แล้ว)
        }

        // ---------------------------------------------------------------- ตาราง segs (กราฟแรงดีด)

        static readonly Regex SegsTable = new Regex(
            @"local\s+(\w+)\s*=\s*\{((?:\s*\{\s*-?[\d.]+\s*,\s*\d+\s*\}\s*,?)+)\s*\}", RegexOptions.Singleline);
        static readonly Regex SegsPair = new Regex(@"\{\s*(-?[\d.]+)\s*,\s*(\d+)\s*\}");
        static readonly Regex SegsRef = new Regex(@"segs\s*=\s*(\w+)\s*,\s*sname\s*=\s*""[^""]*""");

        public static double[] Expand(IEnumerable<(double v, int n)> pairs) =>
            pairs.SelectMany(p => Enumerable.Repeat(p.v, Math.Max(0, p.n))).ToArray();

        public static List<(double v, int n)> Compress(double[] steps)
        {
            var list = new List<(double v, int n)>();
            foreach (double raw in steps)
            {
                double v = Math.Round(raw, 3);
                if (list.Count > 0 && list[^1].v == v) list[^1] = (v, list[^1].n + 1);
                else list.Add((v, 1));
            }
            return list;
        }

        public static bool SameSteps(double[] a, double[] b) =>
            a != null && b != null && a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) < 1e-6);

        // segs ตั้งต้นจากสคริปต์ที่ฝังมา (ไว้ปุ่ม "ค่าเริ่มต้น")
        public static double[] DefaultSteps()
        {
            var t = SegsTable.Match(GunSection.Match(Template()).Value);
            return t.Success ? ParseTable(t.Groups[2].Value) : new double[300];
        }

        static double[] ParseTable(string body) =>
            Expand(SegsPair.Matches(body).Cast<Match>().Select(m => (Num(m.Groups[1].Value), int.Parse(m.Groups[2].Value))));

        static Dictionary<string, double[]> ParseTables(string section) =>
            SegsTable.Matches(section).Cast<Match>()
                .GroupBy(m => m.Groups[1].Value)
                .ToDictionary(g => g.Key, g => ParseTable(g.First().Groups[2].Value));

        static string FormatTable(string name, double[] steps, string nl)
        {
            var pairs = Compress(steps).Select(p => $"{{{Fmt(p.v)},{p.n}}}").ToList();
            var sb = new StringBuilder($"local {name} = {{{nl}");
            for (int i = 0; i < pairs.Count; i += 8)
                sb.Append("  ").Append(string.Join(", ", pairs.Skip(i).Take(8))).Append(',').Append(nl);
            return sb.Append('}').ToString();
        }

        // เขียนตาราง segs ของแต่ละปืนลงสคริปต์ (ต้องรีสตาร์ท G Hub ถึงจะใช้)
        //   ปืนที่กราฟเหมือนกันใช้ตารางเดียวกัน: SEGS_MAIN, SEGS_2, SEGS_3
        public static void WriteSegs(double[][] steps)
        {
            lock (ScriptLock) WriteSegsCore(steps);
        }

        // งานเขียน script.lua (base/x, กราฟ, ติดตั้ง) อ่าน-แก้-เขียนทั้งไฟล์ ถ้ารันพร้อมกันจะเอาค่าเก่าเขียนทับค่าใหม่
        //   → ทำทีละงานเท่านั้น และเขียนผ่านไฟล์ชั่วคราว (โปรแกรมปิดกลางทาง ไฟล์ไม่ขาดครึ่ง)
        static readonly object ScriptLock = new();

        static void WriteScript(string path, string text) => SafeFile.Write(path, text);

        static void WriteSegsCore(double[][] steps)
        {
            var slot = PickScript(ListScripts());
            if (slot == null || !slot.IsGunBeep) throw new InvalidOperationException("ไม่พบสคริปต์ GunBeep ใน G Hub");
            var sec = GunSection.Match(slot.Text);
            if (!sec.Success) throw new InvalidOperationException("ไม่พบตารางปืนในสคริปต์");
            string nl = slot.Text.Contains("\r\n") ? "\r\n" : "\n";
            string s = sec.Value;

            var tables = SegsTable.Matches(s).Cast<Match>().ToList();
            if (tables.Count == 0) throw new InvalidOperationException("ไม่พบตาราง segs ในสคริปต์");

            var names = new string[steps.Length];
            var uniq = new List<(string name, double[] st)>();
            for (int i = 0; i < steps.Length; i++)
            {
                var hit = uniq.FirstOrDefault(u => SameSteps(u.st, steps[i]));
                if (hit.name != null) { names[i] = hit.name; continue; }
                names[i] = uniq.Count == 0 ? "SEGS_MAIN" : "SEGS_" + (i + 1);
                uniq.Add((names[i], steps[i]));
            }

            int at = tables[0].Index;
            for (int k = tables.Count - 1; k >= 0; k--) s = s.Remove(tables[k].Index, tables[k].Length);
            s = s.Insert(at, string.Join(nl + nl, uniq.Select(u => FormatTable(u.name, u.st, nl))));
            s = Regex.Replace(s, @"(\r?\n[ \t]*){3,}", nl + nl);

            int gi = 0;
            s = SegsRef.Replace(s, m =>
            {
                if (gi >= names.Length) return m.Value;
                string n = names[gi++];
                return $"segs = {n}, sname = \"{n}\"";
            });

            string text = slot.Text.Substring(0, sec.Index) + s + slot.Text.Substring(sec.Index + sec.Length);
            if (text == slot.Text) return;
            Backup(Path.Combine(slot.Folder, "script.lua"), "script");
            WriteScript(Path.Combine(slot.Folder, "script.lua"), text);
            slot.Text = text;
            SaveGunSection(slot);
            Log.Info("wrote segs");
        }

        static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        public static string Fmt(double d) => (d + 0.0 == 0 ? 0.0 : d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        // อ่านค่าจากสคริปต์ใน G Hub (ถ้าไม่มี ใช้ saved\guns.lua)
        public static GunVals[] ReadGunValues()
        {
            lock (ScriptLock)
            {
                var v = ReadGunValuesCore(out string from);
                Log.Info($"read guns from {from}: " +
                         (v == null ? "none" : string.Join(" | ", v.Select(g => $"base {Fmt(g.Base)} x {Fmt(g.X)}"))));
                return v;
            }
        }

        static GunVals[] ReadGunValuesCore(out string from)
        {
            var slot = PickScript(ListScripts());
            from = slot != null && slot.IsGunBeep ? "script " + Path.GetFileName(slot.Folder) : "saved\\guns.lua";
            string text = slot != null && slot.IsGunBeep ? slot.Text
                        : File.Exists(SavedGuns) ? File.ReadAllText(SavedGuns, Encoding.UTF8) : null;
            var sec = GunSection.Match(text ?? "");
            if (!sec.Success) return null;
            var tables = ParseTables(sec.Value);
            var vals = GunLine.Matches(sec.Value).Cast<Match>().Take(3).Select(m =>
            {
                int eol = sec.Value.IndexOf('\n', m.Index);
                string line = eol < 0 ? sec.Value.Substring(m.Index) : sec.Value.Substring(m.Index, eol - m.Index);
                var r = SegsRef.Match(line);
                return new GunVals
                {
                    Base = Num(m.Groups[2].Value), X = Num(m.Groups[3].Value),
                    Steps = r.Success && tables.TryGetValue(r.Groups[1].Value, out var st) ? st : null
                };
            }).ToArray();
            return vals.Length > 0 ? vals : null;
        }

        // เขียนค่าที่จูนลงไฟล์สคริปต์ของ G Hub (G Hub ใช้ค่านี้อยู่ในหน่วยความจำแล้ว ไม่ต้องรีสตาร์ท)
        public static void WriteGunValues(GunVals[] vals)
        {
            lock (ScriptLock) WriteGunValuesCore(vals);
        }

        static void WriteGunValuesCore(GunVals[] vals)
        {
            var slot = PickScript(ListScripts());
            if (slot == null || !slot.IsGunBeep) return;
            var sec = GunSection.Match(slot.Text);
            if (!sec.Success) return;
            int i = 0;
            string newSec = GunLine.Replace(sec.Value, m =>
            {
                if (i >= vals.Length) { i++; return m.Value; }
                var v = vals[i++];
                return $"{{ name = \"{m.Groups[1].Value}\", base = {Fmt(v.Base)}, x = {Fmt(v.X)}";
            });
            string text = slot.Text.Substring(0, sec.Index) + newSec + slot.Text.Substring(sec.Index + sec.Length);
            if (text == slot.Text) return;
            WriteScript(Path.Combine(slot.Folder, "script.lua"), text);
            slot.Text = text;
            SaveGunSection(slot);
            Log.Info("wrote guns: " + string.Join(" | ", vals.Select(g => $"base {Fmt(g.Base)} x {Fmt(g.X)}")));
        }

        // ปุ่มวนปืนในสคริปต์ (local CYCLE_BTN = N) — G Hub โหลดใหม่ครั้งหน้าจะได้ปุ่มเดียวกับที่ตั้งใน GunBeep
        static readonly Regex CycleLine = new Regex(@"(local\s+CYCLE_BTN\s*=\s*)\d+");

        public static void WriteCycleButton(int btn)
        {
            lock (ScriptLock)
            {
                var slot = PickScript(ListScripts());
                if (slot == null || !slot.IsGunBeep || !CycleLine.IsMatch(slot.Text)) return;
                string text = CycleLine.Replace(slot.Text, m => m.Groups[1].Value + btn, 1);
                if (text == slot.Text) return;
                WriteScript(Path.Combine(slot.Folder, "script.lua"), text);
                slot.Text = text;
                Log.Info("wrote CYCLE_BTN = " + btn);
            }
        }

        static readonly Regex ShotsLine = new Regex(@"(local\s+MAX_SHOTS\s*=\s*)\d+");

        public static void WriteMaxShots(int n)
        {
            lock (ScriptLock)
            {
                var slot = PickScript(ListScripts());
                if (slot == null || !slot.IsGunBeep || !ShotsLine.IsMatch(slot.Text)) return;
                string text = ShotsLine.Replace(slot.Text, m => m.Groups[1].Value + n, 1);
                if (text == slot.Text) return;
                WriteScript(Path.Combine(slot.Folder, "script.lua"), text);
                slot.Text = text;
                Log.Info("wrote MAX_SHOTS = " + n);
            }
        }

        static readonly Regex AdsLine = new Regex(@"(local\s+REQUIRE_ADS\s*=\s*)(true|false)");

        public static void WriteRequireAds(bool req)
        {
            lock (ScriptLock)
            {
                var slot = PickScript(ListScripts());
                if (slot == null || !slot.IsGunBeep || !AdsLine.IsMatch(slot.Text)) return;
                string text = AdsLine.Replace(slot.Text, m => m.Groups[1].Value + (req ? "true" : "false"), 1);
                if (text == slot.Text) return;
                WriteScript(Path.Combine(slot.Folder, "script.lua"), text);
                slot.Text = text;
                Log.Info("wrote REQUIRE_ADS = " + req);
            }
        }

        public static int AgentPid()
        {
            var p = Process.GetProcessesByName("lghub_agent");
            return p.Length > 0 ? p[0].Id : 0;
        }

        // ตารางปืนที่จูนแล้ว เก็บสำเนาไว้ข้าง GunBeep.exe (saved\guns.lua) ไว้ใช้ตอนลง G Hub / Windows ใหม่
        static readonly string SavedGuns = Path.Combine(Settings.KitDir, "guns.lua");

        static void SaveGunSection(ScriptSlot slot)
        {
            if (!slot.IsGunBeep) return;
            var m = GunSection.Match(slot.Text ?? "");
            if (!m.Success) return;
            try
            {
                if (File.Exists(SavedGuns) && File.ReadAllText(SavedGuns, Encoding.UTF8) == m.Value) return;
                Directory.CreateDirectory(Settings.KitDir);
                SafeFile.Write(SavedGuns, m.Value);
            }
            catch (Exception ex) { Log.Error("save guns.lua", ex); }
        }

        // ใส่สคริปต์ล่าสุด โดยเก็บตารางปืนเดิมของผู้ใช้ไว้ (เรียกตอน G Hub ปิดอยู่)
        //   ตารางปืนเอาจากสคริปต์เดิมใน G Hub ก่อน ถ้าไม่มี (เครื่องใหม่) เอาจาก saved\guns.lua
        public static void InstallScript(ScriptSlot slot)
        {
            lock (ScriptLock) InstallScriptCore(slot);
        }

        static void InstallScriptCore(ScriptSlot slot)
        {
            string template = Template();
            string merged = template;
            var tpl = GunSection.Match(template);
            string lua = Path.Combine(slot.Folder, "script.lua");
            string cur = File.Exists(lua) ? File.ReadAllText(lua, Encoding.UTF8) : slot.Text;   // อ่านใหม่ ไม่ใช้ของที่อ่านไว้ก่อนหน้า
            var old = GunSection.Match(cur ?? "");
            string guns = old.Success ? old.Value
                        : File.Exists(SavedGuns) ? File.ReadAllText(SavedGuns, Encoding.UTF8) : null;
            if (guns != null && tpl.Success)
                merged = template.Substring(0, tpl.Index) + guns + template.Substring(tpl.Index + tpl.Length);

            Backup(lua, "script");
            WriteScript(lua, merged);
            Log.Info("installed script into " + Path.GetFileName(slot.Folder));
        }

        // ---------------------------------------------------------------- โปรไฟล์ PUBG

        static JsonNode FindPubgProfile(JsonNode root)
        {
            var apps = root["applications"]?["applications"]?.AsArray();
            var profiles = root["profiles"]?["profiles"]?.AsArray();
            if (apps == null || profiles == null) return null;

            var pubgIds = apps
                .Where(a => a != null && a.ToJsonString().IndexOf("BATTLEGROUNDS", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(a => a["applicationId"]?.GetValue<string>())
                .Where(id => id != null)
                .ToHashSet();
            if (pubgIds.Count == 0) return null;

            var mine = profiles.Where(p => p != null && pubgIds.Contains(p["applicationId"]?.GetValue<string>())).ToList();
            return mine.FirstOrDefault(p => p["activeForApplication"]?.GetValue<bool>() == true) ?? mine.FirstOrDefault();
        }

        // ตั้ง Persistent Profile = โปรไฟล์ PUBG (เรียกตอน G Hub ปิดอยู่)
        public static void LockPubgProfile()
        {
            var json = ReadSettingsJson(DbPath, copyFirst: false, out long rowId, out bool isBlob);
            var prof = json == null ? null : FindPubgProfile(json);
            if (prof == null) throw new InvalidOperationException("ไม่พบโปรไฟล์ PUBG ใน G Hub");

            json["persistent"] = new JsonObject { ["id"] = prof["id"]?.GetValue<string>() };

            Backup(DbPath, "settings");
            var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string text = json.ToJsonString(opt);

            using var con = new SqliteConnection($"Data Source={DbPath};Pooling=False");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE data SET file = $f WHERE _id = $id";
            cmd.Parameters.AddWithValue("$f", isBlob ? (object)Encoding.UTF8.GetBytes(text) : text);
            cmd.Parameters.AddWithValue("$id", rowId);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("เขียน settings.db ไม่สำเร็จ");
        }

        // อ่าน JSON การตั้งค่าของ G Hub (ตอน G Hub เปิดอยู่ไฟล์ถูกล็อก จึงอ่านจากสำเนา)
        static JsonNode ReadSettingsJson(string path, bool copyFirst, out long rowId, out bool isBlob)
        {
            rowId = 0; isBlob = false;
            if (!File.Exists(path)) return null;

            string src = path;
            string tmp = null;
            if (copyFirst)
            {
                tmp = Path.Combine(Path.GetTempPath(), "gunbeep_ghub_" + Guid.NewGuid().ToString("N") + ".db");
                CopyShared(path, tmp);
                if (File.Exists(path + "-wal")) CopyShared(path + "-wal", tmp + "-wal");
                src = tmp;
            }
            try
            {
                using var con = new SqliteConnection($"Data Source={src};Pooling=False");
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT _id, file FROM data ORDER BY _id DESC LIMIT 1";
                using var rd = cmd.ExecuteReader();
                if (!rd.Read()) return null;
                rowId = rd.GetInt64(0);
                isBlob = rd.GetFieldType(1) == typeof(byte[]);
                string text = isBlob ? Encoding.UTF8.GetString((byte[])rd.GetValue(1)) : rd.GetString(1);
                return JsonNode.Parse(text);
            }
            finally
            {
                if (tmp != null)
                    foreach (var f in new[] { tmp, tmp + "-wal", tmp + "-shm" })
                        try { File.Delete(f); } catch { }
            }
        }

        static void CopyShared(string from, string to)
        {
            using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = File.Create(to);
            src.CopyTo(dst);
        }

        static void Backup(string file, string label)
        {
            Directory.CreateDirectory(BackupDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string ext = Path.GetExtension(file);
            CopyShared(file, Path.Combine(BackupDir, $"{label}-{stamp}{ext}"));
        }
    }

    // ตรวจลายเซ็นดิจิทัลของไฟล์ (WinVerifyTrust)
    static class WinTrust
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Data
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref Data data);

        public static bool Verify(string path)
        {
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var fi = new FileInfo { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = path };
            IntPtr pFi = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(fi, pFi, false);
                var data = new Data
                {
                    cbStruct = (uint)Marshal.SizeOf<Data>(),
                    dwUIChoice = 2,          // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = pFi,
                };
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(pFi);
                Marshal.FreeHGlobal(pFi);
            }
        }
    }
}
