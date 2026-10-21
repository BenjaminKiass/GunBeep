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
        public int OsdMode { get; set; } = 1;       // ป้ายบนจอเกม: 0 ปิด, 1 ตอนเปลี่ยน, 2 ตลอด
        public int OsdCorner { get; set; }          // 0 ซ้ายบน, 1 ขวาบน, 2 ซ้ายล่าง, 3 ขวาล่าง
        public string Preset { get; set; }         // โปรไฟล์ค่าปืนที่โหลด/บันทึกล่าสุด
        public double? Left { get; set; }
        public double? Top { get; set; }
        public string[] Names { get; set; } = { "ปืน 1", "ปืน 2", "ปืน 3" };

        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GunBeep", "settings.json");

        // สำเนาเก็บไว้ข้าง GunBeep.exe (โฟลเดอร์ saved) ลง Windows ใหม่แล้วค่าไม่หาย
        public static readonly string KitDir = Path.Combine(AppContext.BaseDirectory, "saved");
        static readonly string KitPath = Path.Combine(KitDir, "settings.json");

        public static Settings Load()
        {
            foreach (var p in new[] { FilePath, KitPath })
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(p));
                    if (s.Names == null || s.Names.Length < 3) s.Names = new Settings().Names;
                    return s;
                }
                catch { }
            }
            return new Settings();
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
                    File.WriteAllText(p, text);
                }
                catch { }
            }
        }
    }

    // เปิดพร้อม Windows ผ่าน HKCU\...\Run (ไม่ต้องใช้สิทธิ์ admin)
    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "GunBeep";
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
