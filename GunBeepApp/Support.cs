using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

namespace GunBeep
{
    public class Settings
    {
        public bool Sound { get; set; } = true;
        public int HotKey { get; set; } = 0x77;     // F8
        public int Gun { get; set; } = 1;
        public bool TopMost { get; set; }
        public bool RequireAds { get; set; } = false; // true = ดึงเฉพาะตอนเล็งสโคป (ADS), false = ดึงทุกครั้งที่ยิง (ตรงกับสคริปต์ตั้งต้น)
        public int OsdMode { get; set; } = 1;       // ป้ายบนจอเกม: 0 ปิด, 1 ตอนเปลี่ยน, 2 ตลอด
        public int OsdCorner { get; set; }          // 0 ซ้ายบน, 1 ขวาบน, 2 ซ้ายล่าง, 3 ขวาล่าง
        public string Preset { get; set; }         // โปรไฟล์ค่าปืนที่โหลด/บันทึกล่าสุด
        public int PresetKey { get; set; } = 0x78;  // ปุ่มวนโปรไฟล์ในเกม (F9), 0 = ไม่ใช้
        public bool AutoRes { get; set; }           // เปิด PUBG แล้วเปลี่ยนความละเอียด Windows ให้เอง
        public int GameResW { get; set; } = 1728;
        public int GameResH { get; set; } = 1080;
        public int CycleMouse { get; set; } = 4;    // ปุ่มเมาส์วนปืน/จูนในเกม (G Hub): 4 Back, 5 Forward, 3 กลาง, 0 = ไม่ใช้
        public int CycleKey { get; set; }           // หรือปุ่มคีย์บอร์ด (GunBeep จัดการเอง), 0 = ไม่ใช้
        public bool ResAlways { get; set; }
        public int MaxShots { get; set; } = 42;     // ยิงครบกี่นัดแล้วหยุดดึง (แม็ก 40 + เผื่อ 2), 0 = ไม่จำกัด
        // ตรวจสโคปจากหน้ากระเป๋า: ตำแหน่งเป็นสัดส่วนของจอ (x, y = จุดกลาง) — กล่องเลขช่องปืนแผงบน/แผงล่าง, ช่องสโคปแผงบน
        //   ช่องสโคปแผงล่าง = ช่องสโคปแผงบน + ระยะห่างระหว่างกล่องเลขทั้งสอง
        public CalcSettings Calc { get; set; } = new();           // คำนวณตัวคูณแรงดีด (ปืน × สโคป × ของแต่ง × ท่าทาง)
        public bool ScopeAuto { get; set; } = true;
        public string ScopeCalib { get; set; }                   // ความละเอียดที่หาตำแหน่งแผงปืนไว้แล้ว ("1728x1080"), null = ยังไม่หา
        public double[] ScopeBadge1 { get; set; } = { 0.702, 0.101 };
        public double[] ScopeBadge2 { get; set; } = { 0.702, 0.311 };
        public double[] ScopeIcon1 { get; set; } = { 0.849, 0.128 };
        public int ScopeNoneSet { get; set; } = 2;               // ไม่มีสโคป (ศูนย์เหล็ก) ใช้ชุดไหน, 0 = ไม่เปลี่ยน
        public int[] SlotKeys { get; set; }                      // ปุ่มในเกมเลือกปืนช่อง 1 / ช่อง 2 (null = อ่านจาก PUBG)
        public int[] ShotMs { get; set; } = { 86, 86, 86 };      // ตารางรายนัด: เวลาต่อนัดของแต่ละชุดค่า (ms)
        public int[] ShotCount { get; set; } = { 42, 42, 42 };   // ตารางรายนัด: แสดง/แก้กี่นัด
        public int MagMode { get; set; } = 1;       // แม็กหมดแล้วหยุดดึง: 1 นับกระสุนจากเวลากดยิง (MaxShots), 0 ไม่หยุด (เทรนนิ่ง) — 2 (ฟังเสียงปืน) เลิกใช้แล้ว
        public bool Socd { get; set; } = false;      // SOCD: ปุ่มทิศตรงข้ามกดพร้อมกัน (Snap Tap)
        public int SocdMode { get; set; } = 0;       // 0 ปุ่มล่าสุดชนะ, 1 ปุ่มแรกชนะ, 2 กดคู่ = หยุดนิ่ง
        public bool SocdGameOnly { get; set; } = true;
        public List<int[]> SocdPairs { get; set; } = new() { new[] { 0x41, 0x44 } };   // A ⇄ D
        public int[] ScopeKeys { get; set; } = { 0, 0, 0 };   // ปุ่มเลือกชุดค่า 1/2/3 ตรงๆ (ชุดค่าตามสโคป), 0 = ไม่ใช้
        // เก็บของเร็ว: ตำแหน่งเป็นสัดส่วนของจอ (0–1) — ช่องของบนพื้นช่องแรก/ช่องถัดไป, จุดปล่อย (กระเป๋า), จุดกลับ
        public int LootKey { get; set; }                       // ปุ่มสั่งเก็บ (ตอนเปิดกระเป๋า), 0 = ไม่ใช้
        public int LootSlots { get; set; } = 8;                // เก็บกี่ช่องต่อครั้ง
        public int LootSpeed { get; set; } = 1;                // 0 ช้า, 1 กลาง, 2 เร็ว
        public double[] LootStart { get; set; } = { 0.110, 0.147 };
        public double[] LootStep { get; set; } = { 0.110, 0.204 };
        public double[] LootDrop { get; set; } = { 0.750, 0.233 };
        public double[] LootCenter { get; set; } = { 0.480, 0.500 };
        public bool AutoEnglish { get; set; } = true;   // เข้าเกม = สลับคีย์บอร์ดเป็นภาษาอังกฤษ (ภาษาไทยแล้วปุ่ม 1/2/3 ฯลฯ ในเกมไม่ทำงาน), ออกเกม = คืนภาษาเดิม
        public bool PauseInMenu { get; set; } = true; // หยุดดึงชั่วคราวตอนเปิดกระเป๋า/แผนที่/เมนู (มีเคอร์เซอร์) หรือสลับออกจากเกม
        public int SlotSwitch { get; set; } = 2;    // สลับปืนตามช่องในเกม: 0 ปิด, 1 ปุ่ม 1/2, 2 ปุ่ม 1/2 + ลูกกลิ้ง         // ยืดตลอดตั้งแต่เปิด GunBeep (ไม่ใช่แค่ตอนเปิดเกม)
        public bool VisionAuto { get; set; } = false;        // (เลิกใช้: เคยสแกนเองตอนกด Tab แล้วเขียนทับค่าผิด)
        public bool VisionAutoApply { get; set; } = false;   // ส่งค่าแรงดีดที่คำนวณได้เข้า G Hub ทันที (ตอนกดสแกนเอง)
        public int VisionKey { get; set; } = 0;              // (เลิกใช้: ปุ่ม F7 สแกนจอในเกมเอาออกแล้ว)
        public string VisionRes { get; set; } = "1728x1080"; // ความละเอียด ("1728x1080" หรือ "1920x1080")
        public int VisionOffsetX { get; set; } = 0;          // เลื่อนตำแหน่งแนวนอน ±px
        public int VisionOffsetY { get; set; } = 0;          // เลื่อนตำแหน่งแนวตั้ง ±px
        // ---- รุ่นไม่ใช้ G Hub ----
        public int Engine { get; set; } = 0;           // ตัวขยับเมาส์: 0 GunBeep ในตัว (SendInput), 1 G Hub (สำรอง)
        public bool AutoOn { get; set; } = true;       // เปิดมาโครเองตอน PUBG เปิด / ปิดเองตอนเกมปิด (ไม่ต้องกด F8)
        public bool HolsterPause { get; set; } = true; // ถือระเบิด/มีด/เก็บปืน (4 · 5 · X) = ไม่ดึง
        public int Page { get; set; }                  // หน้าที่เปิดค้างไว้ในเมนูซ้าย
        public double? Left { get; set; }
        public double? Top { get; set; }
        public string[] Names { get; set; } = { "ปืน 1", "ปืน 2", "ปืน 3" };

        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GunBeepNative", "settings.json");

        // ตัว G Hub (โปรแกรมแยกอีกตัว) — เปิดรุ่นนี้ครั้งแรกเอาค่าจากตัวนั้นมาใช้ก่อน แต่ไม่เขียนกลับไป
        static readonly string GHubAppPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GunBeep", "settings.json");

        // สำเนาเก็บไว้ข้าง GunBeep.exe (โฟลเดอร์ saved) ลง Windows ใหม่แล้วค่าไม่หาย
        public static readonly string KitDir = Path.Combine(AppContext.BaseDirectory, "saved");
        static readonly string KitPath = Path.Combine(KitDir, "settings.json");

        public static Settings Load()
        {
            foreach (var p in new[] { FilePath, KitPath, GHubAppPath })
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    if (p == GHubAppPath)
                    {
                        // ค่าที่ตัว G Hub ไม่มี (หรือมีแต่ความหมายต่าง) ใช้ค่าเริ่มต้นของรุ่นนี้
                        var g = JsonSerializer.Deserialize<Settings>(File.ReadAllText(p));
                        var d = new Settings();
                        g.Engine = d.Engine; g.AutoOn = d.AutoOn; g.HolsterPause = d.HolsterPause; g.Page = 0;
                        if (g.Names == null || g.Names.Length < 3) g.Names = d.Names;
                        if (g.MagMode == 2) g.MagMode = 1;
                        MigrateCalc(g);
                        Log.Info("settings imported from the G Hub app");
                        return g;
                    }
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(p));
                    if (s.Names == null || s.Names.Length < 3) s.Names = new Settings().Names;
            // ชื่อรวม "Red/Holo" → "Red Dot" (Holo คำนวณแยกเอง ไม่รวมชื่อกัน)
            for (int i = 0; i < s.Names.Length; i++) if (s.Names[i] == "Red/Holo") s.Names[i] = "Red Dot";
                    if (s.MagMode == 2) s.MagMode = 1;          // ฟังเสียงปืนเอาออกแล้ว (ไม่เสถียร) → นับกระสุนแทน
                    MigrateCalc(s);
                    return s;
                }
                catch { }
            }
            return new Settings();
        }

        // ค่าเก่าที่ผมเดาไว้ (−15%, ปืนอ้างอิง M416) → ตัวเลขจากเกม และปืนที่ผู้ใช้จูนจริง (AUG)
        static void MigrateCalc(Settings s)
        {
            var c = s.Calc ??= new CalcSettings();
            if (c.Ver < 1)
            {
                if (c.MuzzlePct == -15) c.MuzzlePct = -10;
                if (c.GripPct == -15) c.GripPct = -10;
                c.RefGun = "AUG";
                c.RefMuzzle = true;
                c.RefGrip = true;
                c.RefStock = false;
                c.Ver = 1;
            }
            if (c.Ver < 2)
            {
                // ตารางจูนกับ AUG + เบรกปากกระบอก + ด้ามจับหัวแม่มือ ด้วยความไวในเกมตอนนี้
                c.RefMuzzleId ??= "brake";
                c.RefGripId ??= "thumb";
                if (GameSettings.Current.Found) RecoilCalc.SnapshotRef(c);
                c.Ver = 2;
            }
            if (c.Ver < 3)
            {
                // ค่าที่เรียนจากมืออย่างเดียวมีเล็งตามเป้าปน (AUG ช่วงติดกันสลับ ×1.49/×0.96, Tommy ×0.5, Beryl ซ้ายขวา +1.69)
                //   → ล้าง แล้วเรียนใหม่ด้วยการวัดจากภาพ (ViewTracker) — ค่าปืนเคาะ (LearnKick) เก็บไว้
                c.Learn.Clear();
                c.LearnX.Clear();
                c.LearnScope.Clear();
                foreach (var k in new List<string>(c.LearnN.Keys)) if (!c.LearnKick.ContainsKey(k)) c.LearnN.Remove(k);
                c.Ver = 3;
            }
            if (c.Ver < 4)
            {
                // ผู้ใช้: ไม่เอาค่าที่เรียนจากมือ → ล้างทั้งหมด (รวมปืนเคาะ) เรียนใหม่จากภาพอย่างเดียว
                c.Learn.Clear();
                c.LearnX.Clear();
                c.LearnScope.Clear();
                c.LearnKick.Clear();
                c.LearnN.Clear();
                c.Ver = 4;
            }
        }

        public void Save()
        {
            var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string text = JsonSerializer.Serialize(this, opt);
            foreach (var p in new[] { FilePath, KitPath })
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    SafeFile.Write(p, text);
                }
                catch (Exception ex) { Log.Error("save settings " + p, ex); }
            }
        }
    }

    // เขียนไฟล์ลงไฟล์ชั่วคราวก่อนแล้วค่อยแทนที่ — ไฟดับ/โปรแกรมพังกลางทาง ไฟล์เดิมยังอยู่ครบ
    public static class SafeFile
    {
        public static void Write(string path, string text, System.Text.Encoding enc = null)
        {
            string tmp = path + ".tmp";
            enc ??= new System.Text.UTF8Encoding(false);
            File.WriteAllText(tmp, text, enc);
            try { File.Move(tmp, path, true); }
            catch (IOException)                   // ไฟล์ปลายทางถูกเปิดค้างไว้ → เขียนทับตรงๆ แทน
            {
                File.WriteAllText(path, text, enc);
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    // บันทึกเหตุการณ์ + ตอนโปรแกรมพัง ลง saved\log.txt (ส่งให้ช่วยดูได้เลย)
    public static class Log
    {
        static readonly string FilePath = Path.Combine(Settings.KitDir, "log.txt");
        static readonly object gate = new();

        public static void Info(string msg) => Write("     ", msg);
        public static void Error(string msg, Exception ex = null) =>
            Write("ERROR", ex == null ? msg : msg + ": " + ex);

        static void Write(string level, string msg)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Settings.KitDir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024)
                        File.Move(FilePath, Path.Combine(Settings.KitDir, "log.old.txt"), true);
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }

    // เปิดพร้อม Windows ผ่าน HKCU\...\Run (ไม่ต้องใช้สิทธิ์ admin)
    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "GunBeepNative";      // ตัว G Hub ใช้ชื่อ "GunBeep" — แยกกัน ไม่ทับกัน
        static string Command => "\"" + Environment.ProcessPath + "\" --tray";

        static string Current()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) as string; }
            catch { return null; }
        }

        public static bool Enabled => Current() != null;

        public static bool Set(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) k.SetValue(Name, Command);
                else k.DeleteValue(Name, false);
                return true;
            }
            catch { return false; }
        }

        // ย้ายโฟลเดอร์ GunBeep แล้ว ให้ชี้ไปที่ตัวที่เปิดอยู่
        public static void Refresh()
        {
            string cur = Current();
            if (cur != null && !string.Equals(cur, Command, StringComparison.OrdinalIgnoreCase)) Set(true);
        }
    }

    // เสียงสังเคราะห์แบบนุ่มๆ (sine + fade) แทนเสียงบี๊บของระบบ
    public static class Sfx
    {
        public static readonly SoundPlayer Ready = Make((660, 80), (880, 80), (1320, 140));
        public static readonly SoundPlayer On    = Make((660, 70), (990, 130));
        public static readonly SoundPlayer Off   = Make((990, 70), (495, 150));
        public static readonly SoundPlayer Up    = Make((1320, 60));
        public static readonly SoundPlayer Down  = Make((700, 60));
        public static readonly SoundPlayer Scope = Make((1100, 70));
        public static readonly SoundPlayer Pick  = Make((880, 90));

        public static void Play(SoundPlayer p, bool enabled)
        {
            if (!enabled) return;
            try { p.Play(); } catch { }
        }

        static SoundPlayer Make(params (double freq, int ms)[] notes)
        {
            const int rate = 44100;
            var data = new List<short>();
            foreach (var (f, ms) in notes)
            {
                int n = rate * ms / 1000;
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Min(1.0, i / (rate * 0.004)) * Math.Exp(-3.0 * t / (ms / 1000.0));
                    double v = (Math.Sin(2 * Math.PI * f * t) + 0.25 * Math.Sin(4 * Math.PI * f * t)) * 0.30 * env;
                    data.Add((short)(v * short.MaxValue));
                }
            }

            var ms2 = new MemoryStream();
            var bw = new BinaryWriter(ms2);
            int bytes = data.Count * 2;
            bw.Write("RIFF"u8.ToArray()); bw.Write(36 + bytes); bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
            bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
            bw.Write("data"u8.ToArray()); bw.Write(bytes);
            foreach (var s in data) bw.Write(s);
            ms2.Position = 0;

            var p = new SoundPlayer(ms2);
            p.Load();
            return p;
        }
    }

    // วาดไอคอนถาด: โลโก้ GunBeep (สี่เหลี่ยมมุมมนไล่สี + เป้าเล็ง) + จุดสถานะมุมขวาล่าง
    //   เปิด = โลโก้สี + จุดเขียว / ปิด = โลโก้เทา + จุดแดง
    // โลโก้ all@Ben: logo.png ข้าง GunBeep.exe (เปลี่ยนเองได้ไม่ต้อง build) หรือที่ฝังมาในโปรแกรม
    public static class Branding
    {
        public static byte[] LogoBytes { get; } = Load();

        static byte[] Load()
        {
            try
            {
                string p = Path.Combine(AppContext.BaseDirectory, "logo.png");
                if (File.Exists(p)) return File.ReadAllBytes(p);
                using var s = typeof(Branding).Assembly.GetManifestResourceStream("logo.png");
                if (s == null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        public static System.Windows.Media.ImageSource LogoImage()
        {
            if (LogoBytes == null) return null;
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(LogoBytes);
                bi.DecodePixelWidth = 160;
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        static System.Drawing.Bitmap small;
        public static System.Drawing.Bitmap Small32()
        {
            if (small != null || LogoBytes == null) return small;
            try
            {
                using var src = new System.Drawing.Bitmap(new MemoryStream(LogoBytes));
                small = new System.Drawing.Bitmap(32, 32);
                using var g = System.Drawing.Graphics.FromImage(small);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // ตัดขอบดำรอบโลโก้ออกนิดหน่อยให้ตัวโลโก้ใหญ่ขึ้นในไอคอนเล็ก
                int cut = src.Width / 14;
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, 32, 32),
                            cut, cut, src.Width - cut * 2, src.Height - cut * 2, System.Drawing.GraphicsUnit.Pixel);
            }
            catch { small = null; }
            return small;
        }
    }

    public static class TrayArt
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public static System.Drawing.Icon Make(bool on, int gun, out System.Windows.Media.ImageSource windowIcon)
        {
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);

                var logo = Branding.Small32();
                if (logo != null)
                {
                    // โลโก้จริง (ตอนปิดมาโครทำเป็นสีเทาจางๆ)
                    using var attrs = new System.Drawing.Imaging.ImageAttributes();
                    if (!on)
                    {
                        float l = 0.3f, a = 0.59f, b = 0.11f;
                        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new[]
                        {
                            new[] { l, l, l, 0f, 0f }, new[] { a, a, a, 0f, 0f }, new[] { b, b, b, 0f, 0f },
                            new[] { 0f, 0f, 0f, 0.8f, 0f }, new[] { 0f, 0f, 0f, 0f, 1f }
                        }));
                    }
                    using (var clip = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        clip.AddEllipse(0, 0, 32, 32);
                        g.SetClip(clip);
                        g.DrawImage(logo, new System.Drawing.Rectangle(0, 0, 32, 32), 0, 0, 32, 32,
                                    System.Drawing.GraphicsUnit.Pixel, attrs);
                        g.ResetClip();
                    }
                }
                else
                {
                // พื้นโลโก้
                var rect = new System.Drawing.RectangleF(1, 1, 30, 30);
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                float r = 9;
                path.AddArc(rect.X, rect.Y, r, r, 180, 90);
                path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
                path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
                path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
                path.CloseFigure();
                var c1 = on ? System.Drawing.Color.FromArgb(29, 107, 255) : System.Drawing.Color.FromArgb(70, 78, 96);
                var c2 = on ? System.Drawing.Color.FromArgb(92, 225, 255) : System.Drawing.Color.FromArgb(104, 112, 130);
                using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(rect, c1, c2, 45f))
                    g.FillPath(bg, path);

                // เป้าเล็ง
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.4f))
                {
                    float cx = 16, cy = 16, rr = 7f, l = 11.5f, gap = 3.2f;
                    g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
                    g.DrawLine(pen, cx, cy - l, cx, cy - gap);
                    g.DrawLine(pen, cx, cy + gap, cx, cy + l);
                    g.DrawLine(pen, cx - l, cy, cx - gap, cy);
                    g.DrawLine(pen, cx + gap, cy, cx + l, cy);
                }
                }

                // จุดสถานะ
                var dot = on ? System.Drawing.Color.FromArgb(34, 197, 94) : System.Drawing.Color.FromArgb(239, 68, 68);
                using (var ring = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(18, 19, 24)))
                    g.FillEllipse(ring, 19, 19, 13, 13);
                using (var br = new System.Drawing.SolidBrush(dot))
                    g.FillEllipse(br, 21, 21, 9, 9);
            }
            IntPtr h = bmp.GetHicon();
            var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone();
            windowIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                h, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            DestroyIcon(h);
            return icon;
        }
    }
}
