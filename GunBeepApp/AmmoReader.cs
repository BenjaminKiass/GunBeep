using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GunBeep
{
    // อ่านจำนวนกระสุนในแม็กจากแถบล่างกลางจอ (ตัวเลขขาวหน้า ∞ / กระสุนสำรอง) — อ่านภาพหน้าจอเท่านั้น ไม่อ่านหน่วยความจำเกม
    //   ตำแหน่งเป็นสัดส่วนของจอ: กลางจอแนวนอน, สูง ~93% — UI ของ PUBG ขยายตามความสูงจอ ใช้ได้ทุกความละเอียด
    public static class AmmoReader
    {
        // กรอบตัวเลข: เริ่มหลังไอคอนกระสุน (กลางจอ −0.03H) กว้างพอเลข 3 หลัก + ∞
        public static Rectangle Region(int w, int h) =>
            new Rectangle((int)(w / 2.0 - 0.030 * h), (int)(0.905 * h), (int)(0.095 * h), (int)(0.045 * h));

        public static Bitmap Capture(int w, int h)
        {
            var r = Region(w, h);
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            return bmp;
        }

        // ---------------------------------------------------------------- แยกหลัก → เทียบรูปร่างกับเลขต้นแบบ
        //   ตัวเลขขาวสว่าง (ไม่มีสี) บนแถบมืด · ∞ = กว้างกว่าสูง → จบตัวเลข · จุด/เส้นเล็ก (ไอคอน, ตัวเลขเลือด) ข้าม
        static bool[,] Ink(Bitmap crop)
        {
            int W = crop.Width, H = crop.Height;
            var ink = new bool[W, H];
            var d = crop.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[d.Stride];
                for (int y = 0; y < H; y++)
                {
                    Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                    for (int x = 0; x < W; x++)
                    {
                        int b = row[x * 3], g = row[x * 3 + 1], r = row[x * 3 + 2];
                        ink[x, y] = r > 190 && g > 190 && b > 190 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 45;
                    }
                }
            }
            finally { crop.UnlockBits(d); }
            return ink;
        }

        public static List<(int x0, int x1, int y0, int y1)> Digits(bool[,] ink)
        {
            int W = ink.GetLength(0), H = ink.GetLength(1);
            var segs = new List<(int x0, int x1, int y0, int y1)>();
            int start = -1;
            for (int x = 0; x <= W; x++)
            {
                bool any = false;
                if (x < W) for (int y = 0; y < H; y++) if (ink[x, y]) { any = true; break; }
                if (any && start < 0) start = x;
                if (!any && start >= 0)
                {
                    int y0 = H, y1 = -1;
                    for (int xx = start; xx < x; xx++) for (int y = 0; y < H; y++) if (ink[xx, y]) { y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
                    segs.Add((start, x - 1, y0, y1));
                    start = -1;
                }
            }
            var digits = new List<(int, int, int, int)>();
            foreach (var s in segs)
            {
                int sw = s.x1 - s.x0 + 1, sh = s.y1 - s.y0 + 1;
                if (sh < H * 0.40) { if (digits.Count > 0 && sw > sh) break; continue; }
                if (sw > sh * 1.25) break;                                   // ∞
                digits.Add(s);
            }
            return digits;
        }

        // ตัวเลขของ PUBG เป็นแบบเหลี่ยม → ดูว่าขีดไหนมีหมึก (แบบเลข 7 ส่วน): บน ขวาบน ขวาล่าง ล่าง ซ้ายล่าง ซ้ายบน กลาง
        static readonly string[] Pattern =
        {
            "1111110", "0110000", "1101101", "1111001", "0110011", "1011011", "1011111", "1110000", "1111111", "1111011",
        };

        static string Segments(bool[,] ink, int x0, int y0, int w, int h)
        {
            double Frac(double fx0, double fx1, double fy0, double fy1)
            {
                int ax = x0 + (int)(fx0 * w), bx = Math.Max(ax + 1, x0 + (int)Math.Ceiling(fx1 * w));
                int ay = y0 + (int)(fy0 * h), by = Math.Max(ay + 1, y0 + (int)Math.Ceiling(fy1 * h));
                int n = 0, c = 0;
                for (int y = ay; y < by; y++)
                    for (int x = ax; x < bx; x++) { n++; if (ink[x, y]) c++; }
                return n == 0 ? 0 : c / (double)n;
            }
            // แต่ละขีดดูเป็นเส้นสั้นๆ ตรงกลางของขีดนั้น — มีหมึกเกินครึ่ง = มีขีด
            double[] f =
            {
                Frac(0.35, 0.65, 0.00, 0.12),   // บน
                Frac(0.80, 1.00, 0.28, 0.40),   // ขวาบน (กลางขีด — เลข 3 มีขีดสั้นที่มุมซ้าย)
                Frac(0.80, 1.00, 0.58, 0.70),   // ขวาล่าง
                Frac(0.35, 0.65, 0.88, 1.00),   // ล่าง
                Frac(0.00, 0.20, 0.58, 0.70),   // ซ้ายล่าง
                Frac(0.00, 0.20, 0.28, 0.40),   // ซ้ายบน
                Frac(0.40, 0.60, 0.42, 0.58),   // กลาง
            };
            var sb = new System.Text.StringBuilder();
            foreach (var v in f) sb.Append(v >= 0.45 ? '1' : '0');
            return sb.ToString();
        }

        public static string LastText;

        // null = อ่านไม่ได้ (ไม่ได้ถือปืน / มีอะไรบัง / ไม่ตรงรูปเลข)
        public static int? Read(Bitmap crop)
        {
            var ink = Ink(crop);
            var ds = Digits(ink);
            if (ds.Count == 0 || ds.Count > 3) { LastText = "-"; return null; }
            int value = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var (x0, x1, y0, y1) in ds)
            {
                int w = x1 - x0 + 1, h = y1 - y0 + 1;
                int dgt;
                if (w < h * 0.32) { dgt = 1; sb.Append("1(narrow) "); }
                else
                {
                    string seg = Segments(ink, x0, y0, w, h);
                    dgt = Array.IndexOf(Pattern, seg);
                    if (dgt < 0) dgt = seg switch
                    {
                        "1100000" or "1110010" => 7,          // 7 ขาเฉียง / มีขีดกลาง
                        "1110011" => 9, "0011111" => 6,       // 9 ไม่มีขีดล่าง / 6 ไม่มีขีดบน
                        "0110111" or "1110011x" => 4,         // 4 ขาซ้ายยาว
                        _ => -1
                    };
                    sb.Append($"{seg}={dgt} ");
                }
                if (dgt < 0) { LastText = sb.ToString(); return null; }
                value = value * 10 + dgt;
            }
            LastText = sb.ToString();
            return value;
        }
        // ภาพดูผล (เครื่องมือตอนพัฒนา)
        public static Bitmap Prepare(Bitmap crop)
        {
            var ink = Ink(crop);
            var bmp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
            var ds = Digits(ink);
            for (int y = 0; y < crop.Height; y++)
                for (int x = 0; x < crop.Width; x++)
                {
                    bool inDigit = ds.Exists(d => x >= d.x0 && x <= d.x1);
                    bmp.SetPixel(x, y, ink[x, y] ? (inDigit ? Color.Black : Color.Red) : Color.White);
                }
            return bmp;
        }    }
}
