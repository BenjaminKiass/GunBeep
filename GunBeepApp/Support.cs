using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GunBeep
{
    public class Settings
    {
        public bool Sound { get; set; } = true;
        public int HotKey { get; set; } = 0x77;     // F8
        public int Gun { get; set; } = 1;
        public bool TopMost { get; set; }
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

                // พื้นโลโก้
                var rect = new System.Drawing.RectangleF(1, 1, 30, 30);
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                float r = 9;
                path.AddArc(rect.X, rect.Y, r, r, 180, 90);
                path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
                path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
                path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
                path.CloseFigure();
                var c1 = on ? System.Drawing.Color.FromArgb(99, 102, 241) : System.Drawing.Color.FromArgb(82, 82, 91);
                var c2 = on ? System.Drawing.Color.FromArgb(34, 211, 238) : System.Drawing.Color.FromArgb(113, 113, 122);
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
