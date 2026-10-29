using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;

namespace GunBeep
{
    // ตรวจสโคปจากหน้ากระเป๋า (Tab): ดูแผงปืนสองแผงทางขวา
    //   เลขช่อง (1 / 2) = รูปร่างตัวเลขในกล่อง · ชื่อปืน = Windows OCR · สโคป = เทียบกับภาพที่ผู้ใช้สอนไว้
    //   ถ่ายภาพจอเฉพาะช่องเล็กๆ แบบเดียวกับโปรแกรมแคปจอ — ไม่แตะตัวเกม ไม่อ่านหน่วยความจำ
    public static class ScopeDetect
    {
        // ขนาดกล่อง (สัดส่วนของความสูงจอ — UI ของ PUBG ขยายตามความสูง)
        public const double BadgeSize = 0.028, IconSize = 0.048, NameW = 0.20, NameH = 0.034;
        const int F = 40;                             // ภาพสโคปย่อเหลือ 40x40 ก่อนเทียบ

        public sealed class Template
        {
            public int Set { get; set; }              // 1–3 = ชุดค่า, 0 = ไม่มีสโคป
            public string Label { get; set; }
            public float[] Feature { get; set; }
            public string Image { get; set; }         // ไฟล์ภาพตัวอย่าง (ไว้โชว์ในหน้าตั้งค่า)
        }

        public sealed class Panel
        {
            public int Slot;                          // 1 / 2, 0 = ไม่มีปืน / อ่านไม่ออก
            public int Set = -1;                      // 1–3, 0 = ไม่มีสโคป, -1 = ไม่รู้จัก, -2 = รู้จักแต่ไม่มีการ์ดชุดนี้
            public Bitmap Icon;                       // ภาพช่องสโคปล่าสุด
            public bool[] Att;                        // ของแต่ง: [0] ปากกระบอก, [1] กริป, [3] พานท้าย
            public double Score;
            public string ScopeLabel = "?";
            public string Name = "";
            public override string ToString() => $"slot {Slot} {Name} scope {ScopeLabel} ({Score:0.00}) set {Set}";
        }

        static readonly string Dir = Path.Combine(Settings.KitDir, "scopes");
        static readonly string JsonPath = Path.Combine(Dir, "scopes.json");
        static List<Template> templates;
        static readonly object gate = new();

        public static List<Template> Templates
        {
            get
            {
                lock (gate)
                {
                    if (templates != null) return templates;
                    try { templates = File.Exists(JsonPath) ? JsonSerializer.Deserialize<List<Template>>(File.ReadAllText(JsonPath)) : null; }
                    catch (Exception ex) { Log.Error("load scopes", ex); }
                    return templates ??= new List<Template>();
                }
            }
        }

        public static string ImagePath(Template t) => Path.Combine(Dir, t.Image ?? "");

        static void SaveTemplates()
        {
            lock (gate)
            {
                Directory.CreateDirectory(Dir);
                SafeFile.Write(JsonPath, JsonSerializer.Serialize(templates));
            }
        }

        public static void Teach(Bitmap icon, int set, string label)
        {
            var t = new Template { Set = set, Label = label, Feature = Feature(icon), Image = $"scope-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png" };
            Directory.CreateDirectory(Dir);
            icon.Save(ImagePath(t), ImageFormat.Png);
            lock (gate) Templates.Add(t);
            SaveTemplates();
            Log.Info($"scope taught: {label} → set {set} ({Templates.Count} examples)");
        }

        public static void Forget(Template t)
        {
            lock (gate) Templates.Remove(t);
            try { File.Delete(ImagePath(t)); } catch { }
            SaveTemplates();
        }

        // ---------------------------------------------------------------- ตำแหน่งบนจอ
        public static Rectangle Box(double cx, double cy, double sizeH, int w, int h, double widthH = -1)
        {
            int bw = (int)Math.Round((widthH < 0 ? sizeH : widthH) * h), bh = (int)Math.Round(sizeH * h);
            return new Rectangle((int)Math.Round(cx * w - bw / 2.0), (int)Math.Round(cy * h - bh / 2.0), Math.Max(4, bw), Math.Max(4, bh));
        }

        public static (Rectangle badge, Rectangle name, Rectangle icon)[] Boxes(Settings cfg, int w, int h)
        {
            double dy = cfg.ScopeBadge2[1] - cfg.ScopeBadge1[1], dx = cfg.ScopeBadge2[0] - cfg.ScopeBadge1[0];
            (Rectangle, Rectangle, Rectangle) One(double bx, double by, double ix, double iy)
            {
                var badge = Box(bx, by, BadgeSize, w, h);
                // ชื่อปืนอยู่ขวาของกล่องเลข
                var name = new Rectangle(badge.Right + (int)(0.004 * h), badge.Y - (int)(0.003 * h), (int)(NameW * h), (int)(NameH * h));
                return (badge, name, Box(ix, iy, IconSize, w, h));
            }
            return new[]
            {
                One(cfg.ScopeBadge1[0], cfg.ScopeBadge1[1], cfg.ScopeIcon1[0], cfg.ScopeIcon1[1]),
                One(cfg.ScopeBadge2[0], cfg.ScopeBadge2[1], cfg.ScopeIcon1[0] + dx, cfg.ScopeIcon1[1] + dy),
            };
        }

        // ---------------------------------------------------------------- ภาพ → ลักษณะ
        // ขอบภาพ (gradient) ย่อ 40x40 แล้ว normalize — ทนต่อความสว่าง/พื้นหลังโปร่งแสงที่ต่างกัน
        public static float[] Feature(Bitmap src)
        {
            using var small = new Bitmap(F + 2, F + 2, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, new Rectangle(0, 0, F + 2, F + 2));
            }
            var gray = new float[(F + 2) * (F + 2)];
            var data = small.LockBits(new Rectangle(0, 0, F + 2, F + 2), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[data.Stride];
                for (int y = 0; y < F + 2; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    for (int x = 0; x < F + 2; x++)
                        gray[y * (F + 2) + x] = (row[x * 3] + row[x * 3 + 1] + row[x * 3 + 2]) / 765f;
                }
            }
            finally { small.UnlockBits(data); }
            var f = new float[F * F];
            for (int y = 1; y <= F; y++)
                for (int x = 1; x <= F; x++)
                {
                    float gx = gray[y * (F + 2) + x + 1] - gray[y * (F + 2) + x - 1];
                    float gy = gray[(y + 1) * (F + 2) + x] - gray[(y - 1) * (F + 2) + x];
                    f[(y - 1) * F + x - 1] = MathF.Sqrt(gx * gx + gy * gy);
                }
            float mean = f.Average();
            double norm = 0;
            for (int i = 0; i < f.Length; i++) { f[i] -= mean; norm += f[i] * f[i]; }
            norm = Math.Sqrt(norm);
            if (norm > 1e-6) for (int i = 0; i < f.Length; i++) f[i] = (float)(f[i] / norm);
            return f;
        }

        static double Dot(float[] a, float[] b)
        {
            double s = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
            return s;
        }

        // ช่องว่าง (ไม่มีของแต่ง) = แทบไม่มีขอบ/ลายในภาพ
        public static double Detail(Bitmap src)
        {
            double sum = 0, sum2 = 0;
            int n = 0;
            for (int y = 0; y < src.Height; y += 2)
                for (int x = 0; x < src.Width; x += 2)
                {
                    var p = src.GetPixel(x, y);
                    double l = (p.R + p.G + p.B) / 765.0;
                    sum += l; sum2 += l * l; n++;
                }
            double m = sum / n;
            return Math.Sqrt(Math.Max(0, sum2 / n - m * m));
        }

        // ---------------------------------------------------------------- สโคปที่รู้จักในตัว (ไม่ต้องสอน)
        //   ลักษณะ (ตัวเลข) ของสโคปที่ติดบนปืนจริง: 4x, 3x, 6x — ชุดค่าเลือกจากชื่อการ์ด (เช่นการ์ดชื่อ "4x")
        sealed class Builtin { public string Label { get; set; } public float[] Feature { get; set; } }
        static List<Builtin> builtins;

        static List<Builtin> Builtins
        {
            get
            {
                if (builtins != null) return builtins;
                try
                {
                    using var s = typeof(ScopeDetect).Assembly.GetManifestResourceStream("scopes-builtin.json");
                    builtins = s == null ? new() : JsonSerializer.Deserialize<List<Builtin>>(s);
                }
                catch (Exception ex) { Log.Error("builtin scopes", ex); builtins = new(); }
                return builtins;
            }
        }

        // การ์ดชุดค่าไหนชื่อมีสโคปนี้ (เช่น "4x", "Red/Holo") — ไม่มี = -2 (รู้จักแต่ไม่มีชุด ไม่เปลี่ยน)
        public static int SetFor(string label, string[] names)
        {
            if (names == null) return -2;
            string l = label.ToLowerInvariant();
            for (int i = 0; i < names.Length; i++)
            {
                string n = (names[i] ?? "").ToLowerInvariant().Replace(" ", "");
                if (n.Split('/', ',', '+', '|', '·').Any(p => p == l || p == l.Replace("x", "")) || n.Contains(l)) return i + 1;
                if ((l == "reddot" && n.Contains("red")) || (l == "holo" && n.Contains("holo"))) return i + 1;
            }
            return -2;
        }

        // เทียบภาพ: ตัวอย่างที่ผู้ใช้สอน (หรือเรียนจากปุ่มเลือกสโคป) มาก่อน ไม่เจอค่อยใช้ที่รู้จักในตัว
        //   ต้องคะแนนพอและห่างจากชุดอื่นพอ ไม่งั้น = ไม่รู้จัก
        public static (Template best, double score, double margin) Match(Bitmap icon, string[] names = null)
        {
            var f = Feature(icon);
            List<Template> list;
            lock (gate) list = Templates.ToList();
            var user = Best(f, list);
            if (user.best != null && user.score >= 0.55) return user;
            var bi = Best(f, Builtins.Select(b => new Template { Label = b.Label, Feature = b.Feature, Set = SetFor(b.Label, names) }).ToList());
            return bi.best != null && bi.score > user.score ? bi : user;
        }

        static (Template best, double score, double margin) Best(float[] f, List<Template> list)
        {
            Template best = null;
            double bs = -1, other = -1;
            foreach (var t in list)
            {
                double s = Dot(f, t.Feature);
                if (s > bs) { if (best != null && best.Label != t.Label) other = Math.Max(other, bs); best = t; bs = s; }
                else if (best != null && t.Label != best.Label) other = Math.Max(other, s);
            }
            return (best, bs, other < 0 ? bs : bs - other);
        }

        // ---------------------------------------------------------------- หาตำแหน่งแผงปืนเอง (OCR ชื่อปืน)
        public static string[] GunWordList => GunWords;
        static readonly string[] GunWords =
        {
            "AKM", "M416", "BERYL", "MK12", "SCAR", "AUG", "ACE32", "QBZ", "G36C", "K2", "FAMAS", "M16A4", "GROZA", "MK47",
            "SLR", "SKS", "MINI14", "MK14", "QBU", "VSS", "UMP", "VECTOR", "MICRO", "UZI", "TOMMY", "MP5K", "PP19", "BIZON", "P90",
            "JS9", "MP9", "DP28", "M249", "MG3", "KAR98K", "M24", "AWM", "MOSIN", "LYNX", "WIN94", "S12K", "S1897", "S686", "DBS",
            "O12", "SAWED", "DRAGUNOV", "P92", "P1911", "P18C", "R1895", "R45", "DEAGLE", "SKORPION", "CROSSBOW", "PANZERFAUST", "M79", "MGL",
        };

        static bool IsGunWord(string w)
        {
            string n = new string(w.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (n.Length < 2) return false;
            string fixedN = n.Replace('O', '0').Replace('I', '1').Replace('L', '1');
            return GunWords.Any(g => n == g || fixedN == g.Replace('O', '0').Replace('I', '1').Replace('L', '1') || (g.Length >= 4 && n.StartsWith(g)));
        }

        // เปิดกระเป๋าครั้งแรก (หรือเปลี่ยนความละเอียด): อ่านตัวหนังสือฝั่งขวาของจอ หาชื่อปืน → คำนวณตำแหน่งกล่องเลข/ช่องสโคป
        //   ตำแหน่งอิงจาก UI ของ PUBG (ขนาดตามความสูงจอ): กล่องเลขอยู่ซ้ายชื่อ 0.0205H, ช่องสโคปห่างกล่องเลข (0.261H, 0.027H)
        public static async Task<bool> AutoLocate(Settings cfg)
        {
            if (Ocr() == null) return false;
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            var area = new Rectangle(w / 2, 0, w - w / 2, (int)(h * 0.75));
            using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            return await LocateIn(cfg, bmp, area.X, w, h);
        }

        // กล่องเลขช่อง = กรอบขาวบาง ข้างในดำ: หาตำแหน่งในระยะ ±10 px ที่ขอบสว่างกว่าข้างในมากที่สุด
        static (double x, double y) Snap(Bitmap bmp, int offsetX, int w, int h, (double x, double y) est)
        {
            int s = (int)Math.Round(BadgeSize * h);
            int cx = (int)Math.Round(est.x * w) - offsetX, cy = (int)Math.Round(est.y * h);
            double Lum(int x, int y)
            {
                if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) return 0;
                var p = bmp.GetPixel(x, y);
                return (p.R + p.G + p.B) / 765.0;
            }
            double best = double.MinValue;
            int bx = cx, by = cy;
            for (int dy = -10; dy <= 10; dy++)
                for (int dx = -10; dx <= 10; dx++)
                {
                    int x0 = cx + dx - s / 2, y0 = cy + dy - s / 2, x1 = x0 + s - 1, y1 = y0 + s - 1;
                    double edge = 0, inner = 0;
                    for (int i = 0; i < s; i++)
                    {
                        edge += Lum(x0 + i, y0) + Lum(x0 + i, y1) + Lum(x0, y0 + i) + Lum(x1, y0 + i);
                        inner += Lum(x0 + i, y0 + 3) + Lum(x0 + i, y1 - 3) + Lum(x0 + 3, y0 + i) + Lum(x1 - 3, y0 + i);
                    }
                    double score = (edge - inner) / (4.0 * s);
                    if (score > best) { best = score; bx = cx + dx; by = cy + dy; }
                }
            return best > 0.15 ? ((bx + offsetX) / (double)w, by / (double)h) : est;   // ไม่เจอกรอบชัด = ใช้ค่าประมาณ
        }

        // หาตำแหน่งจากภาพ (ฝั่งขวาของจอ เริ่มที่ x = offsetX) — แยกออกมาไว้ทดสอบกับภาพแคปได้
        public static async Task<bool> LocateIn(Settings cfg, Bitmap bmp, int offsetX, int w, int h)
        {
            if (Ocr() == null) return false;
            var area = new Rectangle(offsetX, 0, bmp.Width, bmp.Height);
            var found = new List<(double x, double y)>();
            try
            {
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await ras.WriteAsync(ms.ToArray().AsBuffer());
                ras.Seek(0);
                var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
                using var sb = await dec.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                var r = await ocr.RecognizeAsync(sb);
                foreach (var line in r.Lines)
                {
                    var word = line.Words.FirstOrDefault(x => IsGunWord(x.Text));
                    if (word == null) continue;
                    var b = word.BoundingRect;
                    if (b.Height < h * 0.012) continue;                          // ตัวเล็ก = ไม่ใช่หัวแผงปืน (เช่นข้อความฆ่า)
                    found.Add(((area.X + b.X) / w - 0.0205 * h / w, (b.Y + b.Height / 2) / h));
                }
            }
            catch (Exception ex) { Log.Error("auto locate", ex); return false; }
            // แผงปืนหลักคือสองอันบนสุดในแนวเดียวกัน (ปืนพก/ระเบิดอยู่ล่าง)
            found = found.OrderBy(p => p.y).ToList();
            if (found.Count == 0) return false;
            var first = found[0];
            if (!found.All(p => Math.Abs(p.x - first.x) < 0.02 || p.y - first.y > 0.5)) found = found.Where(p => Math.Abs(p.x - first.x) < 0.02).ToList();
            const double SPACING = 0.2097;                                       // ระยะระหว่างแผงปืน (สัดส่วนความสูงจอ)
            var second = found.Skip(1).FirstOrDefault(p => Math.Abs(p.y - first.y - SPACING) < 0.03);
            // ตำแหน่งจากตัวหนังสือคลาดได้ 3–5 px → ขยับหากรอบสี่เหลี่ยมขาวของกล่องเลขให้ตรงเป๊ะ
            first = Snap(bmp, area.X, w, h, first);
            if (second != default) second = Snap(bmp, area.X, w, h, second);
            cfg.ScopeBadge1 = new[] { Math.Round(first.x, 4), Math.Round(first.y, 4) };
            cfg.ScopeBadge2 = second != default ? new[] { Math.Round(second.x, 4), Math.Round(second.y, 4) }
                                                : new[] { Math.Round(first.x, 4), Math.Round(first.y + SPACING, 4) };
            cfg.ScopeIcon1 = new[] { Math.Round(first.x + 0.261 * h / w, 4), Math.Round(first.y + 0.027, 4) };
            cfg.ScopeCalib = $"{w}x{h}";
            Log.Info($"scope auto-locate {cfg.ScopeCalib}: badge1 {first.x:0.000},{first.y:0.000} ({found.Count} gun names)");
            return true;
        }

        // ---------------------------------------------------------------- เลขช่อง 1 / 2
        // ตัวเลขขาวในกล่องดำ: "1" แคบ, "2" กว้างและมีขีดล่างเต็ม — ไม่ใช่ 1/2 (เช่น 3 ปืนพก) = 0
        public static int ReadSlot(Bitmap badge)
        {
            int w = badge.Width, h = badge.Height;
            // ตัดขอบกล่องสีขาวออก (กรอบ ~12% ต่อด้าน)
            int m = Math.Max(2, (int)(w * 0.18));
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1, ink = 0;
            var on = new bool[w, h];
            for (int y = m; y < h - m; y++)
                for (int x = m; x < w - m; x++)
                {
                    var p = badge.GetPixel(x, y);
                    if (p.R + p.G + p.B < 3 * 150) continue;
                    on[x, y] = true;
                    ink++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            if (ink < 4 || maxY - minY < h * 0.25) return 0;
            double bw = maxX - minX + 1, bh = maxY - minY + 1;
            // แถวล่างสุดของตัวเลข: "2" มีขีดเต็มความกว้าง
            int bottom = 0;
            for (int x = minX; x <= maxX; x++) if (on[x, maxY] || on[x, Math.Max(minY, maxY - 1)]) bottom++;
            // แถวกลาง: "3" มีขีดกลาง, "2" เป็นเส้นทแยงสั้น
            int midRow = (minY + maxY) / 2, mid = 0;
            for (int x = minX; x <= maxX; x++) if (on[x, midRow]) mid++;
            if (bw / bh < 0.42) return 1;
            if (bottom >= bw * 0.7 && mid <= bw * 0.5) return 2;
            return 0;
        }

        // ---------------------------------------------------------------- ชื่อปืน (Windows OCR ภาษาอังกฤษ)
        static Windows.Media.Ocr.OcrEngine ocr;
        static bool ocrTried;

        static Windows.Media.Ocr.OcrEngine Ocr()
        {
            if (!ocrTried)
            {
                ocrTried = true;
                try { ocr = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")); }
                catch (Exception ex) { Log.Error("ocr", ex); }
                if (ocr == null) Log.Info("ocr: English OCR not available");
            }
            return ocr;
        }

        public static async Task<string> ReadName(Bitmap name)
        {
            if (Ocr() == null) return "";
            try
            {
                using var big = new Bitmap(name.Width * 3, name.Height * 3, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(name, new Rectangle(0, 0, big.Width, big.Height));
                }
                using var ms = new MemoryStream();
                big.Save(ms, ImageFormat.Png);
                using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await ras.WriteAsync(ms.ToArray().AsBuffer());
                ras.Seek(0);
                var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
                using var sb = await dec.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                var r = await ocr.RecognizeAsync(sb);
                return r.Text.Trim();
            }
            catch (Exception ex) { Log.Error("ocr read", ex); return ""; }
        }

        // ---------------------------------------------------------------- อ่านทั้งแผง
        public static Bitmap Crop(Bitmap src, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
            if (r.Width < 2 || r.Height < 2) return new Bitmap(2, 2);
            return src.Clone(r, PixelFormat.Format24bppRgb);
        }

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

        // ถ่ายเฉพาะพื้นที่แผงปืนทางขวา (ไม่ถ่ายทั้งจอ)
        public static Bitmap CaptureArea(Settings cfg, out Point origin)
        {
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            var boxes = Boxes(cfg, w, h);
            var all = boxes.SelectMany(b => new[] { b.badge, b.name, b.icon }).Append(RecoilCalc.AttachmentArea(cfg, w, h)).Aggregate(Rectangle.Union);
            all.Intersect(new Rectangle(0, 0, w, h));
            origin = all.Location;
            if (all.Width < 4 || all.Height < 4) return null;
            var bmp = new Bitmap(all.Width, all.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(all.Location, Point.Empty, all.Size);
            return bmp;
        }

        // ---------------------------------------------------------------- ปุ่มเลือกปืนช่อง 1 / 2 จากไฟล์ตั้งค่า PUBG
        //   GameUserSettings.ini: (ActionName="Weapon_1",Keys=((Key=Three),...)) — ไฟล์บนดิสก์ ไม่ได้อ่านจากตัวเกม
        public static int[] ReadPubgSlotKeys()
        {
            try
            {
                string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        "TslGame", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
                if (!File.Exists(f)) return null;
                string s;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs)) s = r.ReadToEnd();
                string[] names = { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };
                int Key(int n)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(s, $@"ActionName=""Weapon_{n}"",Keys=\(\(Key=(\w+)");
                    if (!m.Success) return 0;
                    int d = Array.IndexOf(names, m.Groups[1].Value);
                    return d >= 0 ? 0x30 + d : 0;
                }
                int k1 = Key(1), k2 = Key(2);
                return k1 != 0 && k2 != 0 && k1 != k2 ? new[] { k1, k2 } : null;
            }
            catch (Exception ex) { Log.Error("read pubg keys", ex); return null; }
        }

        public static Panel[] Read(Settings cfg, Bitmap shot, Point origin, int screenW, int screenH)
        {
            var boxes = Boxes(cfg, screenW, screenH);
            var res = new Panel[2];
            for (int i = 0; i < 2; i++)
            {
                var (badge, _, icon) = boxes[i];
                badge.Offset(-origin.X, -origin.Y);
                icon.Offset(-origin.X, -origin.Y);
                var p = new Panel();
                using (var b = Crop(shot, badge)) p.Slot = ReadSlot(b);
                using (var ic = Crop(shot, icon))
                {
                    if (Detail(ic) < 0.035) { p.Set = 0; p.ScopeLabel = "ไม่มีสโคป"; p.Score = 1; }
                    else
                    {
                        // ลองเลื่อนกรอบ ±2–4 px เผื่อตั้งจุดเพี้ยนนิดหน่อย เอาตำแหน่งที่ตรงที่สุด
                        int d = Math.Max(2, icon.Width / 14);
                        (Template t, double s, double margin) best = (null, -1, 0);
                        foreach (int ox in new[] { 0, -d, d })
                            foreach (int oy in new[] { 0, -d, d })
                            {
                                var r = icon;
                                r.Offset(ox, oy);
                                using var c = (ox | oy) == 0 ? null : Crop(shot, r);
                                var m = Match(c ?? ic, cfg.Names);
                                if (m.score > best.s) best = m;
                            }
                        p.Score = best.s;
                        if (best.t != null && best.s >= 0.55 && best.margin >= 0.04) { p.Set = best.t.Set; p.ScopeLabel = best.t.Label; }
                    }
                    p.Icon = new Bitmap(ic);                      // เก็บไว้เรียนรู้ ถ้าผู้ใช้กดปุ่มเลือกสโคปเอง
                }
                if (p.Slot != 0) p.Att = RecoilCalc.ReadAttachments(cfg, shot, origin, screenW, screenH, i);
                res[i] = p;
            }
            return res;
        }
    }
}
