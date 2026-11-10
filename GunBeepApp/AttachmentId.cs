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

        // ===== ข้อมูลเดิม v1 สำรองไว้กันบั๊ก / Fallback =====
        public static readonly Item[] Items_v1 =
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
            new("folding", "พานท้ายพับได้", "stock", 20), new("cheek", "แผ่นรองแก้ม (DMR, SR)", "stock", 20),
        };

        // ===== ข้อมูล v2 ปัจจุบัน =====
        public static readonly Item[] Items =
        {
            new("comp_ar", "ตัวชดเชย (AR)", "muzzle", 15), new("flash_ar", "ตัวลดแสง (AR)", "muzzle", 10),
            new("supp_ar", "ตัวเก็บเสียง (AR)", "muzzle", 0), new("brake", "เบรกปากกระบอก", "muzzle", 10),
            new("comp_dmr", "ตัวชดเชย (DMR)", "muzzle", 20), new("flash_dmr", "ตัวลดแสง (DMR)", "muzzle", 10),
            new("supp_dmr", "ตัวเก็บเสียง (DMR)", "muzzle", 0), new("comp_smg", "ตัวชดเชย (SMG)", "muzzle", 25),
            new("flash_smg", "ตัวลดแสง (SMG)", "muzzle", 10), new("supp_smg", "ตัวเก็บเสียง (SMG)", "muzzle", 0),
            // "angled" = กริปเอียง: Update 41.1 (เม.ย. 2026) เอา Angled Foregrip ออกจากของเกิดในแมพ แล้วเพิ่ม Tilted Grip (แนวตั้ง +12%)
            //   ของที่เจอในแมพตอนนี้คือ Tilted Grip → ใช้ id เดิม (ต้นแบบที่ผู้ใช้สอนไว้ยังใช้ได้) ค่าแนวตั้ง 12 ตรงกับ Tilted Grip
            new("vertical", "กริปแนวตั้ง", "grip", 15), new("angled", "กริปเอียง (Tilted)", "grip", 12),
            new("half", "กริปแบบครึ่ง", "grip", 8), new("thumb", "กริปหัวแม่มือ", "grip", 10),
            new("light", "กริปน้ำหนักเบา", "grip", 10), new("laser", "เลเซอร์", "grip", 0),
            new("tactical", "พานท้ายยุทธวิธี", "stock", 10), new("heavy", "พานท้ายขนาดใหญ่", "stock", 10),
            new("folding", "พานท้ายพับได้", "stock", 20), new("cheek", "แผ่นรองแก้ม (DMR, SR)", "stock", 20),
        };

        public static Item Get(string id) => Items.FirstOrDefault(i => i.Id == id) ?? Items_v1.FirstOrDefault(i => i.Id == id);

        // ---------------------------------------------------------------- ผู้ใช้สอน: กดเลือกชนิดในหน้ามาโคร → จำลักษณะของภาพช่องจริงนี้
        static readonly string UserPath = Path.Combine(Settings.KitDir, "attachments.json");
        static readonly object userGate = new();
        static List<Tpl> user;

        static List<Tpl> User
        {
            get
            {
                if (user != null) return user;
                try { user = File.Exists(UserPath) ? JsonSerializer.Deserialize<List<Tpl>>(File.ReadAllText(UserPath)) : new(); }
                catch (Exception ex) { Log.Error("user attachments", ex); user = new(); }
                return user ??= new();
            }
        }

        public static bool Teach(Bitmap slotCrop, string id)
        {
            var f = SlotFeature(slotCrop);
            if (f == null || Get(id) == null) return false;
            lock (userGate)
            {
                User.Add(new Tpl { Id = id, Feature = f, Real = true });
                try { Directory.CreateDirectory(Settings.KitDir); File.WriteAllText(UserPath, JsonSerializer.Serialize(User)); }
                catch (Exception ex) { Log.Error("save user attachments", ex); }
            }
            Log.Info($"attachment taught: {id} ({User.Count(t => t.Id == id)} examples)");
            return true;
        }

        // Real = จากช่องของแต่งบนแผงปืนจริง (แม่น); ไม่ใช่ = จากภาพป้ายรายละเอียด (หน้าตาต่าง ต้องเหมือนมากกว่า)
        sealed class Tpl { public string Id { get; set; } public float[] Feature { get; set; } public bool Real { get; set; } }
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

        // ตรวจสอบความเข้ากันได้ของของแต่งกับประเภทปืนจริงใน PUBG (กันตรวจผิดหมวด เช่น M24 ติดลดแสง AR)
        public static bool IsCompatible(string gunKey, int slot, string itemId)
        {
            if (string.IsNullOrEmpty(gunKey)) return true;
            var it = Get(itemId);
            if (it == null) return false;

            // 0. ปากกระบอก (slot 0)
            if (slot == 0)
            {
                if (!RecoilCalc.HasMuzzleSlot(gunKey)) return false;
                if (gunKey == "GROZA") return itemId == "supp_ar";
                if (gunKey == "TOMMY") return itemId == "supp_smg";

                bool isAr = RecoilCalc.IsAr(gunKey) || gunKey is "S12K" or "O12";
                bool isSmg = RecoilCalc.IsSmg(gunKey);
                bool isDmr = RecoilCalc.IsDmr(gunKey);
                bool isSr = RecoilCalc.IsSr(gunKey);

                if (itemId == "brake") return isAr || isDmr; // เบรกปากกระบอกใส่ได้เฉพาะ AR และ DMR (รวมถึง S12K/O12 ไม่ให้ติดกับ SR หรือ SMG)
                if (isSr) return itemId.EndsWith("_dmr");     // สไนเปอร์ (M24, Kar98k, etc.) ใส่ได้เฉพาะปาก DMR/SR เท่านั้น
                if (isAr) return itemId.EndsWith("_ar");      // AR ใส่ได้เฉพาะปาก AR
                if (isSmg) return itemId.EndsWith("_smg");    // SMG ใส่ได้เฉพาะปาก SMG
                if (isDmr) return itemId.EndsWith("_dmr") || itemId.EndsWith("_ar"); // DMR ใส่ได้ทั้ง AR และ DMR
                return false;
            }

            // 1. กริป (slot 1)
            if (slot == 1)
            {
                if (!RecoilCalc.HasGripSlot(gunKey)) return false;
                if (gunKey == "TOMMY") return itemId == "vertical"; // ทอมมี่ใส่ได้เฉพาะกริปแนวตั้ง
                // จาก pubg.wiki.gg (ข้อมูลปืน patch 22.2): Vector = half/laser/light/vertical, G36C = angled/laser/light/vertical
                if (gunKey == "VECTOR") return itemId is not ("angled" or "thumb");
                if (gunKey == "G36C") return itemId is not ("half" or "thumb");
                return true;
            }

            // 2. แม็ก (slot 2)
            if (slot == 2)
            {
                if (!RecoilCalc.HasMagSlot(gunKey)) return false;
                return true;
            }

            // 3. พานท้าย (slot 3)
            if (slot == 3)
            {
                if (!RecoilCalc.HasStockSlot(gunKey)) return false;
                // DMR และ SR: SKS, SLR, Mk14, Dragunov, VSS และ SR ทุกตัว ใส่แผ่นรองแก้มเท่านั้น
                if (RecoilCalc.IsSr(gunKey) || gunKey is "SLR" or "SKS" or "MK14" or "DRAGUNOV" or "VSS") return itemId == "cheek";
                // Micro Uzi ใส่พานท้ายพับได้เท่านั้น
                if (gunKey is "MICRO" or "UZI") return itemId == "folding";
                // MP5K ใส่ได้ทั้ง tactical, heavy, folding
                if (gunKey == "MP5K") return itemId is "tactical" or "heavy" or "folding";
                // M416, ACE32, M16A4, Mk47, Vector, M249 ใส่พานท้ายยุทธวิธี หรือพานท้ายขนาดใหญ่
                // RPD (Update 42.3, ส.ค. 2026): ใส่พานท้าย AR/LMG ได้ทุกแบบ
                if (gunKey is "M416" or "ACE32" or "M16A4" or "MK47" or "VECTOR" or "M249" or "RPD") return itemId is "tactical" or "heavy";
                return false;
            }

            return true;
        }

        // ช่อง 0 ปากกระบอก, 1 กริป, 3 พานท้าย → ชิ้นที่ตรงที่สุด (null = ไม่แน่ใจ ใช้ % กลาง)
        public static (Item item, double score, double margin) Identify(Bitmap slotCrop, int slot, string gunKey = null)
        {
            string cat = slot == 0 ? "muzzle" : slot == 1 ? "grip" : slot == 3 ? "stock" : null;
            if (cat == null) return (null, 0, 0);
            var f = SlotFeature(slotCrop);
            if (f == null) return (null, 0, 0);
            Item best = null;
            double bs = -1, other = -1;
            List<Tpl> userList;
            lock (userGate) userList = User.ToList();
            int compatibleItems = Items.Count(it => it.Slot == cat && IsCompatible(gunKey, slot, it.Id));
            foreach (var t in userList.Concat(Builtins))
            {
                var it = Get(t.Id);
                if (it == null || it.Slot != cat) continue;
                if (!IsCompatible(gunKey, slot, it.Id)) continue; // กรองของแต่งที่ไม่ตรงประเภทปืนทิ้ง
                double s = Dot(f, t.Feature) - (t.Real ? 0 : TipPenalty);
                if (s > bs) { if (best != null && best.Id != it.Id) other = Math.Max(other, bs); best = it; bs = s; }
                else if (best != null && it.Id != best.Id) other = Math.Max(other, s);
            }
            // ถ้าของชิ้นนั้นเป็นของแต่งชิ้นเดียวที่เป็นไปได้ของปืนนี้ (เช่น cheek บน M24) เกณฑ์ความมั่นใจจะยืดหยุ่นขึ้น
            double reqMargin = compatibleItems <= 1 ? 0.0 : MinMargin;
            double reqScore = compatibleItems <= 1 ? 0.45 : MinScore;
            double margin = other < 0 ? bs : bs - other;
            return bs >= reqScore && margin >= reqMargin ? (best, bs, margin) : (null, bs, margin);
        }

        // เดิม 0.30 / 0.02 (ต้นแบบจากภาพป้ายอย่างเดียว) → อ่านพื้นหลังเป็น "กริปเบา", กริปหัวแม่มือเป็น "ครึ่ง"
        public const double MinScore = 0.62, MinMargin = 0.05, TipPenalty = 0.15;

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
