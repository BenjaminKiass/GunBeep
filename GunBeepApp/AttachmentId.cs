using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GunBeep
{
    // รู้ว่าช่องของแต่งติดชิ้นไหน (ตัวชดเชย / ตัวลดแสง / เก็บเสียง / เบรก, กริปแต่ละแบบ, พานท้าย)
    //   เทียบภาพไอคอนในช่องกับลักษณะ (ตัวเลข) ที่ทำจากภาพในหน้าต่างอธิบายของในเกม — ไม่ได้เก็บภาพของเกมไว้ในโปรแกรม
    //   % คุมแรงดีดแนวตั้งของแต่ละชิ้น = ตัวเลขจากหน้าต่างอธิบายของในเกม (ผู้ใช้แคปมา 2026-10-30)
    public static class AttachmentId
    {
        public sealed class Item
        {
            public string Id, Name, Slot;      // Slot: muzzle / grip / stock
            public double Vertical;            // % คุมแรงดีดแนวตั้ง (+15 = แรงดึง ×0.85)
            public Item(string id, string name, string slot, double v) { Id = id; Name = name; Slot = slot; Vertical = v; }
        }

        public static readonly Item[] Items =
        {
            new("comp_ar", "ตัวชดเชย (AR)", "muzzle", 15), new("flash_ar", "ตัวลดแสง (AR)", "muzzle", 10),
            new("supp_ar", "ตัวเก็บเสียง (AR)", "muzzle", 0), new("brake", "เบรกปากกระบอก", "muzzle", 10),
            new("comp_dmr", "ตัวชดเชย (DMR)", "muzzle", 20), new("flash_dmr", "ตัวลดแสง (DMR)", "muzzle", 10),
            new("supp_dmr", "ตัวเก็บเสียง (DMR)", "muzzle", 0), new("comp_smg", "ตัวชดเชย (SMG)", "muzzle", 25),
            new("flash_smg", "ตัวลดแสง (SMG)", "muzzle", 10), new("supp_smg", "ตัวเก็บเสียง (SMG)", "muzzle", 0),
            new("vertical", "กริปแนวตั้ง", "grip", 15), new("angled", "กริปแบบเอียง", "grip", 12),
            new("half", "กริปแบบครึ่ง", "grip", 8), new("thumb", "กริปหัวแม่มือ", "grip", 10),
            new("light", "กริปน้ำหนักเบา", "grip", 10), new("laser", "เลเซอร์", "grip", 0),
            new("tactical", "พานท้ายยุทธวิธี", "stock", 10), new("heavy", "พานท้ายขนาดใหญ่", "stock", 10),
            new("folding", "พานท้ายพับได้", "stock", 20),
        };

        public static Item Get(string id) => Items.FirstOrDefault(i => i.Id == id);

        sealed class Tpl { public string Id { get; set; } public float[] Feature { get; set; } }
        static List<Tpl> builtins;

        static List<Tpl> Builtins
        {
            get
            {
                if (builtins != null) return builtins;
                try
                {
                    using var s = typeof(AttachmentId).Assembly.GetManifestResourceStream("attachments-builtin.json");
                    builtins = s == null ? new() : JsonSerializer.Deserialize<List<Tpl>>(s);
                }
                catch (Exception ex) { Log.Error("builtin attachments", ex); builtins = new(); }
                return builtins;
            }
        }

        // ช่อง 0 ปากกระบอก, 1 กริป, 3 พานท้าย → ชิ้นที่ตรงที่สุด (null = ไม่แน่ใจ ใช้ % กลาง)
        public static (Item item, double score, double margin) Identify(Bitmap slotCrop, int slot)
        {
            string cat = slot == 0 ? "muzzle" : slot == 1 ? "grip" : slot == 3 ? "stock" : null;
            if (cat == null) return (null, 0, 0);
            var f = SlotFeature(slotCrop);
            if (f == null) return (null, 0, 0);
            Item best = null;
            double bs = -1, other = -1;
            foreach (var t in Builtins)
            {
                var it = Get(t.Id);
                if (it == null || it.Slot != cat) continue;
                double s = Dot(f, t.Feature);
                if (s > bs) { if (best != null && best.Id != it.Id) other = Math.Max(other, bs); best = it; bs = s; }
                else if (best != null && it.Id != best.Id) other = Math.Max(other, s);
            }
            double margin = other < 0 ? bs : bs - other;
            return bs >= MinScore && margin >= MinMargin ? (best, bs, margin) : (null, bs, margin);
        }

        public const double MinScore = 0.30, MinMargin = 0.02;

        static double Dot(float[] a, float[] b)
        {
            double s = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
            return s;
        }

        // ---------------------------------------------------------------- ภาพ → ลักษณะ (ตัดเฉพาะตัวของ วางกลางกรอบสี่เหลี่ยม)
        static float[,] Lum(Bitmap src)
        {
            using var b = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(b)) g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var l = new float[b.Height, b.Width];
            try
            {
                var row = new byte[d.Stride];
                for (int y = 0; y < b.Height; y++)
                {
                    Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                    for (int x = 0; x < b.Width; x++) l[y, x] = (row[x * 3] + row[x * 3 + 1] + row[x * 3 + 2]) / 765f;
                }
            }
            finally { b.UnlockBits(d); }
            return l;
        }

        static float Median(IEnumerable<float> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? 0.5f : a[a.Length / 2]; }

        static float[] SquareFeature(float[,] l, Func<int, int, bool> mask, int x0, int y0, int x1, int y1, float bg)
        {
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1, n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (mask(x, y)) { n++; bx0 = Math.Min(bx0, x); by0 = Math.Min(by0, y); bx1 = Math.Max(bx1, x); by1 = Math.Max(by1, y); }
            if (n < 8) return null;
            int w = bx1 - bx0 + 1, h = by1 - by0 + 1, s = (int)(Math.Max(w, h) * 1.16) + 2;
            using var sq = new Bitmap(s, s, PixelFormat.Format24bppRgb);
            var d = sq.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[d.Stride];
                int ox = (s - w) / 2, oy = (s - h) / 2;
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        int sx = x - ox + bx0, sy = y - oy + by0;
                        float v = sx >= bx0 && sx <= bx1 && sy >= by0 && sy <= by1 ? l[sy, sx] : bg;
                        byte c = (byte)Math.Clamp(v * 255, 0, 255);
                        row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = c;
                    }
                    Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, d.Stride);
                }
            }
            finally { sq.UnlockBits(d); }
            return ImageFeature.Feature(sq);
        }

        // ไอคอนในช่องของแต่ง: พื้นเทาโปร่งแสง ตัวของมีทั้งส่วนมืดและสว่าง → เอาพิกเซลที่ต่างจากพื้นชัดๆ
        public static float[] SlotFeature(Bitmap crop)
        {
            var l = Lum(crop);
            int H = l.GetLength(0), W = l.GetLength(1), m = Math.Max(2, W / 16);
            var all = new List<float>();
            for (int y = m; y < H - m; y++) for (int x = m; x < W - m; x++) all.Add(l[y, x]);
            float bg = Median(all);
            return SquareFeature(l, (x, y) => Math.Abs(l[y, x] - bg) > 0.10f, m, m, W - m, H - m, bg);
        }

        // ภาพในหน้าต่างอธิบาย: แผ่นสีเทาอ่อน ตัวของเป็นก้อนสูงสุดตรงกลาง (ตัวหนังสือเป็นแถบบางๆ)
        public static float[] TooltipFeature(Bitmap shot)
        {
            var l = Lum(shot);
            int H = l.GetLength(0), W = l.GetLength(1);
            // คอลัมน์ของแผ่นอธิบาย = ช่วงยาวสุดที่ค่ากลางของคอลัมน์สว่าง
            int bestA = 0, bestB = W, runA = -1;
            int bestLen = 0;
            for (int x = 0; x <= W; x++)
            {
                bool light = x < W && Median(Enumerable.Range(0, H).Select(y => l[y, x])) > 0.70f;
                if (light && runA < 0) runA = x;
                if (!light && runA >= 0) { if (x - runA > bestLen) { bestLen = x - runA; bestA = runA; bestB = x; } runA = -1; }
            }
            int xa = bestA + 4, xb = bestB - 4;
            float bg = Median(Enumerable.Range(0, H).SelectMany(y => new[] { l[y, xa], l[y, xb - 1] }));
            bool Dark(int x, int y) => l[y, x] < bg - 0.18f;
            // แถวที่มีของมืด → แถบต่อเนื่อง (เว้นได้ 3 แถว) แถบสูงสุดในช่วง 15–88% = ตัวของ
            var rows = Enumerable.Range(0, H).Select(y => Enumerable.Range(xa, xb - xa).Any(x => Dark(x, y))).ToArray();
            int ba = 0, bb = 0, a = -1, gap = 0;
            for (int y = (int)(H * 0.15); y <= (int)(H * 0.88); y++)
            {
                bool r = y < H && rows[y];
                if (r) { if (a < 0) a = y; gap = 0; }
                else if (a >= 0 && ++gap > 3)
                {
                    int end = y - gap + 1;
                    if (end - a > bb - ba) { ba = a; bb = end; }
                    a = -1; gap = 0;
                }
            }
            if (a >= 0 && (int)(H * 0.88) - a > bb - ba) { ba = a; bb = (int)(H * 0.88); }
            if (bb - ba < 15) return null;
            return SquareFeature(l, Dark, xa, ba, xb, bb, bg);
        }

        // ---------------------------------------------------------------- สร้างไฟล์ลักษณะในตัว (ตอนพัฒนา)
        //   GunBeep.exe --att-build <โฟลเดอร์ภาพ> <map.txt: เลขรูป=id> <out.json> [ทดสอบ: ภาพ x,y,ขนาด,ช่อง ...]
        public static void Build(string[] args)
        {
            var sb = new StringBuilder();
            var files = Directory.GetFiles(args[1], "*.png").OrderBy(f => f).ToArray();
            var map = File.ReadAllLines(args[2]).Select(s => s.Split('=')).Where(p => p.Length == 2)
                          .ToDictionary(p => int.Parse(p[0].Trim()), p => p[1].Trim());
            var list = new List<Tpl>();
            for (int i = 0; i < files.Length; i++)
            {
                if (!map.TryGetValue(i + 1, out string id)) continue;
                using var bmp = new Bitmap(files[i]);
                var f = TooltipFeature(bmp);
                sb.AppendLine($"#{i + 1} {id}: {(f == null ? "NO OBJECT" : "ok")}");
                if (f != null) list.Add(new Tpl { Id = id, Feature = f.Select(v => (float)Math.Round(v, 4)).ToArray() });
            }
            File.WriteAllText(args[3], JsonSerializer.Serialize(list));
            builtins = list;
            // ทดสอบ: ภาพ, แล้วตามด้วย x,y,size,slot หลายชุด
            if (args.Length > 5)
            {
                using var img = new Bitmap(args[4]);
                foreach (var spec in args.Skip(5))
                {
                    var p = spec.Split(',').Select(int.Parse).ToArray();
                    using var c = ImageFeature.Crop(img, new Rectangle(p[0], p[1], p[2], p[2]));
                    var r = Identify(c, p[3]);
                    var f = SlotFeature(c);
                    var scores = list.Where(t => Get(t.Id)?.Slot == (p[3] == 0 ? "muzzle" : p[3] == 1 ? "grip" : "stock"))
                                     .Select(t => (t.Id, s: Dot(f, t.Feature))).OrderByDescending(t => t.s).Take(6);
                    sb.AppendLine($"test {spec}: {(r.item?.Id ?? "?")} score {r.score:0.000} margin {r.margin:0.000} | " +
                                  string.Join(" ", scores.Select(t => $"{t.Id}={t.s:0.000}")));
                }
            }
            File.WriteAllText(args[3] + ".txt", sb.ToString());
        }
    }
}
