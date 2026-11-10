using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace GunBeep
{
    // อ่านการตั้งค่าของ PUBG จากไฟล์บนดิสก์ (GameUserSettings.ini) — อ่านอย่างเดียว ไม่แก้ไฟล์เกม ไม่แตะตัวเกม
    //   ความไว (ทั่วไป / เล็ง / แต่ละสโคป), ความไวเดียวทุกสโคป, ตัวคูณแนวตั้ง, โหมดเล็ง, ปุ่มเลือกอาวุธ / ย่อ / หมอบ
    //   ตรวจจับการเปลี่ยนแปลงแบบ Real-time ด้วย FileSystemWatcher ทันทีที่กด Apply ในเกม PUBG
    public sealed class StanceBinding
    {
        public int Vk;
        public bool Shift;
        public bool Ctrl;
        public bool Alt;

        public bool Matches(int vk, bool shift, bool ctrl, bool alt)
        {
            if (Vk != vk || Ctrl != ctrl || Alt != alt) return false;
            // ถ้าปุ่มในเกมระบุว่าต้องกด Shift (เช่น Shift + S) -> ต้องกด Shift
            // ถ้าปุ่มไม่ได้ระบุ Shift (เช่น C ปกติ) -> กด C เดี่ยวๆ หรือกด Shift+C (ตอนวิ่งสไลด์) ให้นับเป็นการย่อทั้งคู่
            return Shift ? shift : true;
        }

        public override string ToString() =>
            $"{(Ctrl ? "Ctrl+" : "")}{(Alt ? "Alt+" : "")}{(Shift ? "Shift+" : "")}{(Vk >= 0x41 && Vk <= 0x5A ? ((char)Vk).ToString() : $"0x{Vk:X2}")}";
    }

    public sealed class GameSettings
    {
        public bool Found;
        public double General = 50, Aim = 50, Ads = 50, AdsMagnified = 50, Vertical = 1;
        public bool Universal = true;                                // ความไวเดียวทุกสโคป
        public readonly Dictionary<string, double> Scope = new();    // Scope2X … Scope15X (ค่าที่ตั้งไว้ แม้ไม่ได้ใช้)
        public string AdsMode = "Hold";
        public int Slot1 = 0x31, Slot2 = 0x32, Pistol = 0x33, Melee = 0x34, Throwable = 0x35, Holster = 0x58,
                   Crouch = 'C', Prone = 'Z', HoldBreath = 0xA0, ReloadKey = 'R';
        public readonly List<StanceBinding> CrouchBindings = new();
        public readonly List<StanceBinding> ProneBindings = new();

        public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                      "TslGame", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");

        static GameSettings cache;
        static DateTime cacheFile;
        static FileSystemWatcher watcher;
        static Timer watcherDebounce;
        static readonly object syncLock = new();

        static void InitWatcher()
        {
            if (watcher != null) return;
            lock (syncLock)
            {
                if (watcher != null) return;
                try
                {
                    string dir = Path.GetDirectoryName(FilePath);
                    if (!Directory.Exists(dir)) return;
                    watcher = new FileSystemWatcher(dir, Path.GetFileName(FilePath))
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        EnableRaisingEvents = true
                    };
                    void OnFileChanged(object s, FileSystemEventArgs e)
                    {
                        lock (syncLock)
                        {
                            watcherDebounce?.Dispose();
                            watcherDebounce = new Timer(_ =>
                            {
                                try
                                {
                                    Reload();
                                    Log.Info("pubg settings: reloaded in real time via FileSystemWatcher");
                                }
                                catch (Exception ex) { Log.Error("watcher reload", ex); }
                            }, null, 60, Timeout.Infinite);
                        }
                    }
                    watcher.Changed += OnFileChanged;
                    watcher.Created += OnFileChanged;
                    watcher.Renamed += (s, e) => OnFileChanged(s, e);
                }
                catch (Exception ex) { Log.Error("init pubg settings watcher", ex); }
            }
        }

        // อ่านใหม่เมื่อไฟล์เปลี่ยน (เกมเขียนไฟล์ตอนกด "ใช้" ในหน้าตั้งค่า) — Real-time ไม่หน่วง 3 วิ
        public static GameSettings Current
        {
            get
            {
                InitWatcher();
                DateTime t = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (cache != null && t == cacheFile) return cache;
                cacheFile = t;
                cache = Load();
                Changed?.Invoke();
                return cache;
            }
        }

        public static event Action Changed;
        public static void Reload()
        {
            cache = null;
            cacheFile = DateTime.MinValue;
            _ = Current;
        }

        // ความไวที่สโคปนี้ใช้จริง ("Scoping" = เล็ง 1x/Red/Holo, "Scope4X" …)
        public double SensFor(string sensName)
        {
            if (sensName == "Scoping") return Ads;
            if (Universal) return AdsMagnified;
            return Scope.TryGetValue(sensName, out double v) ? v : AdsMagnified;
        }

        static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        static Match Last(string s, string pattern)
        {
            Match last = null;
            foreach (Match m in Regex.Matches(s, pattern)) last = m;
            return last;
        }

        static GameSettings Load()
        {
            var g = new GameSettings();
            try
            {
                if (!File.Exists(FilePath)) return g;
                string s;
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs)) s = r.ReadToEnd();
                g.Found = true;

                int map = s.LastIndexOf("SensitiveMap=((Mouse", StringComparison.Ordinal);
                if (map >= 0)
                {
                    string part = s.Substring(map, Math.Min(2500, s.Length - map));
                    foreach (Match m in Regex.Matches(part, @"SensitiveName=""(\w+)"",Sensitivity=([\d.]+)"))
                    {
                        double v = Num(m.Groups[2].Value);
                        switch (m.Groups[1].Value)
                        {
                            case "Normal": g.General = v; break;
                            case "Targeting": g.Aim = v; break;
                            case "Scoping": g.Ads = v; break;
                            case "ScopingMagnified": g.AdsMagnified = v; break;
                            default: g.Scope[m.Groups[1].Value] = v; break;
                        }
                    }
                    var per = Last(s.Substring(0, map), @"bIsUsingPerScopeMouseSensitivity=(\w+)");
                    if (per != null) g.Universal = per.Groups[1].Value == "False";
                }
                var vert = Last(s, @"MouseVerticalSensitivityMultiplierAdjusted=([\d.]+)");
                if (vert != null) g.Vertical = Num(vert.Groups[1].Value);
                var ads = Last(s, @"InputModeADS=(\w+)");
                if (ads != null) g.AdsMode = ads.Groups[1].Value;

                int Key(string action, int def)
                {
                    var m = Last(s, $@"ActionName=""{action}"",Keys=\(\(Key=(\w+)");
                    return m == null ? def : Vk(m.Groups[1].Value) is int vk and > 0 ? vk : def;
                }
                g.Slot1 = Key("Weapon_1", g.Slot1);
                g.Slot2 = Key("Weapon_2", g.Slot2);
                g.Pistol = Key("Weapon_3", g.Pistol);
                g.Melee = Key("Weapon_4", g.Melee);
                g.Throwable = Key("Weapon_5", g.Throwable);
                g.Holster = Key("UnarmedAll", Key("HolsterWeapon", g.Holster));
                List<StanceBinding> Bindings(string action, int defVk)
                {
                    var list = new List<StanceBinding>();
                    var m = Last(s, $@"ActionName=""{action}"",Keys=\((.+?\)\))(?=\)|\z)");
                    if (m != null)
                    {
                        foreach (Match p in Regex.Matches(m.Groups[1].Value, @"\(([^)]*Key=\w+[^)]*)\)"))
                        {
                            var km = Regex.Match(p.Groups[1].Value, @"Key=(\w+)");
                            if (km.Success && Vk(km.Groups[1].Value) is int vk && vk > 0)
                            {
                                bool sh = p.Groups[1].Value.Contains("bShift=True");
                                bool ct = p.Groups[1].Value.Contains("bCtrl=True");
                                bool al = p.Groups[1].Value.Contains("bAlt=True");
                                list.Add(new StanceBinding { Vk = vk, Shift = sh, Ctrl = ct, Alt = al });
                            }
                        }
                    }
                    if (list.Count == 0 && defVk > 0)
                        list.Add(new StanceBinding { Vk = defVk, Shift = false, Ctrl = false, Alt = false });
                    return list;
                }

                void AddUniqueBindings(List<StanceBinding> target, List<StanceBinding> source)
                {
                    foreach (var b in source)
                        if (!target.Any(x => x.Vk == b.Vk && x.Shift == b.Shift && x.Ctrl == b.Ctrl && x.Alt == b.Alt))
                            target.Add(b);
                }

                AddUniqueBindings(g.CrouchBindings, Bindings("ToggleCrouch", g.Crouch));
                AddUniqueBindings(g.CrouchBindings, Bindings("HoldCrouch", 0));
                AddUniqueBindings(g.ProneBindings, Bindings("ToggleProne", g.Prone));
                AddUniqueBindings(g.ProneBindings, Bindings("HoldProne", 0));
                if (g.CrouchBindings.Count > 0) g.Crouch = g.CrouchBindings[0].Vk;
                if (g.ProneBindings.Count > 0) g.Prone = g.ProneBindings[0].Vk;
                g.HoldBreath = Key("HoldBreath", g.HoldBreath);
                g.ReloadKey = Key("Reload", g.ReloadKey);
            }
            catch (Exception ex) { Log.Error("read pubg settings", ex); }
            return g;
        }

        static readonly string[] Digits = { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };

        // ชื่อปุ่มของ Unreal → virtual key ของ Windows
        public static int Vk(string k)
        {
            int d = Array.IndexOf(Digits, k);
            if (d >= 0) return 0x30 + d;
            if (k.Length == 1 && char.IsLetter(k[0])) return char.ToUpperInvariant(k[0]);
            if (k.Length >= 2 && k[0] == 'F' && int.TryParse(k.Substring(1), out int f) && f >= 1 && f <= 24) return 0x6F + f;
            return k switch
            {
                "LeftShift" => 0xA0, "RightShift" => 0xA1, "LeftControl" => 0xA2, "RightControl" => 0xA3,
                "LeftAlt" => 0xA4, "RightAlt" => 0xA5, "SpaceBar" => 0x20, "Tab" => 0x09, "CapsLock" => 0x14,
                "LeftMouseButton" => 0x01, "RightMouseButton" => 0x02, "MiddleMouseButton" => 0x04,
                "ThumbMouseButton" => 0x05, "ThumbMouseButton2" => 0x06, "Tilde" => 0xC0, "Enter" => 0x0D,
                _ => 0
            };
        }
    }
}
