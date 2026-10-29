using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GunBeep
{
    // คำนวณตัวคูณแรงดึง: ปืน × สโคป × ของแต่ง (ส่งให้สคริปต์ G Hub เป็น GEAR_MULT) และท่าทาง (STANCE_MULT)
    //   ตารางในการ์ดชุดค่า = จูนไว้กับสโคปของการ์ดนั้น + ของแต่งชุดอ้างอิง (ตั้งได้) ตอนยืน
    //   สโคป: แรงที่ต้องดึง ∝ กำลังขยาย / ความไวของสโคปนั้น (อ่านจากไฟล์ตั้งค่า PUBG)
    //     ความไวแปลงแบบ PUBG: 0.02 × 10^((ค่า − 50) / 50)  (ตรงกับ LastConvertedSensitivity ในไฟล์)
    //   ของแต่ง/ท่าทาง/ปืน: % ที่ตั้งได้ (ค่าเริ่มต้นเป็นค่าประมาณ ไม่ใช่ตัวเลขทางการของเกม)
    public sealed class CalcSettings
    {
        public bool On { get; set; } = true;
        // ตัวเลขจากคำอธิบายของในเกม: เบรกปากกระบอก / ด้ามจับหัวแม่มือ = คุมแรงดีดแนวตั้ง +10% → แรงดึง −10%
        public double MuzzlePct { get; set; } = -10;
        public double GripPct { get; set; } = -10;
        public double StockPct { get; set; } = -8;
        public bool RefMuzzle { get; set; } = true;          // ตารางที่จูนไว้ จูนตอนติดปากกระบอก
        public bool RefGrip { get; set; } = true;            // ... ติดกริป
        public bool RefStock { get; set; } = false;          // ... ติดพานท้าย
        public double CrouchPct { get; set; } = -28;
        public double PronePct { get; set; } = -45;
        public Dictionary<string, double> Guns { get; set; } = new();   // ชื่อปืน → ปรับเพิ่มจากค่าในตัว (1 = ตามค่าในตัว)
        public string RefGun { get; set; } = "AUG";          // ตารางในการ์ดชุดค่าจูนไว้กับปืนนี้ (ผู้ใช้จูนกับ AUG + เบรกปากกระบอก + ด้ามจับ)
        public int Ver { get; set; }                          // 1 = ค่าของแต่งจากเกม, 2 = ชนิดของแต่ง + ความไวตอนจูน
        // ของแต่งตอนจูนตาราง (ชิ้นที่รู้จัก) และแรงที่สโคปแต่ละตัวต้องใช้ตอนนั้น (จากความไวในเกมตอนจูน)
        public string RefMuzzleId { get; set; } = "brake";
        public string RefGripId { get; set; } = "thumb";
        public string RefStockId { get; set; }
        public Dictionary<string, double> RefNeed { get; set; } = new();
    }

    public static class RecoilCalc
    {
        // ---------------------------------------------------------------- สโคป
        public static readonly (string label, double mag, string sensName)[] Scopes =
        {
            ("reddot", 1, "Scoping"), ("holo", 1, "Scoping"), ("2x", 2, "Scope2X"), ("3x", 3, "Scope3X"),
            ("4x", 4, "Scope4X"), ("6x", 6, "Scope6X"), ("8x", 8, "Scope8X"), ("15x", 15, "Scope15X"),
        };

        // ความไวที่แต่ละสโคปใช้จริง จากการตั้งค่าเกม (ความไวเดียวทุกสโคป = ทุกสโคปขยายใช้ค่าเล็งขยาย)
        public static Dictionary<string, double> Sens
        {
            get
            {
                var g = GameSettings.Current;
                var d = new Dictionary<string, double>();
                foreach (var sc in Scopes) d[sc.sensName] = g.SensFor(sc.sensName);
                return d;
            }
        }

        // ความไวเปลี่ยนจากตอนจูน → คูณเท่าไหร่ (เช่น ลดความไว 4x ในเกม = ต้องดึงแรงขึ้น)
        public static double SensFactor(CalcSettings c, string label)
        {
            if (label == null || c.RefNeed == null || !c.RefNeed.TryGetValue(label, out double r) || r <= 0) return 1;
            double n = Need(label);
            return double.IsFinite(n) && n > 0 ? n / r : 1;
        }

        public static void SnapshotRef(CalcSettings c)
        {
            c.RefNeed = new();
            foreach (var sc in Scopes) { double n = Need(sc.label); if (double.IsFinite(n) && n > 0) c.RefNeed[sc.label] = Math.Round(n, 4); }
        }
        static double Conv(double s) => 0.02 * Math.Pow(10, (s - 50) / 50);

        public static (string label, double mag, string sensName)? Scope(string label) =>
            Scopes.Where(s => s.label == (label ?? "").ToLowerInvariant()).Select(s => ((string, double, string)?)s).FirstOrDefault();

        // แรงที่ต้องดึง (สัมพัทธ์) ของสโคปนี้
        public static double Need(string label)
        {
            var sc = Scope(label);
            if (sc == null) return double.NaN;
            double s = Sens.TryGetValue(sc.Value.sensName, out double v) ? v : 50;
            return sc.Value.mag / Conv(s) / Math.Max(0.1, GameSettings.Current.Vertical);
        }

        // สโคปของการ์ดชุดค่า (จากชื่อการ์ด เช่น "4x", "Red/Holo")
        public static string SetScope(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            foreach (var sc in Scopes.OrderByDescending(s => s.label.Length))
                if (n.Contains(sc.label) && !(sc.label == "4x" && n.Contains("14x"))) return sc.label;
            if (n.Contains("red") || n.Contains("dot")) return "reddot";
            return null;
        }

        // สโคปที่ตรวจเจอ → ใช้การ์ดไหน และคูณเท่าไหร่: มีการ์ดของสโคปนี้ = ×1, ไม่มี = การ์ดที่กำลังขยายใกล้สุด × อัตราส่วน
        public static (int set, double mult, string viaScope) ForScope(string label, string[] names)
        {
            var sc = Scope(label);
            if (sc == null || names == null) return (-1, 1, null);
            int best = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < names.Length; i++)
            {
                var ss = Scope(SetScope(names[i]));
                if (ss == null) continue;
                if (ss.Value.label == sc.Value.label || (ss.Value.mag == 1 && sc.Value.mag == 1)) return (i + 1, 1, ss.Value.label);
                double dist = Math.Abs(Math.Log(ss.Value.mag / sc.Value.mag));
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            if (best < 0) return (-1, 1, null);
            string via = SetScope(names[best]);
            double r = Need(label) / Need(via);
            return (best + 1, double.IsFinite(r) ? r : 1, via);
        }

        // ---------------------------------------------------------------- ปืน
        // แรงดีดแนวตั้งโดยประมาณของแต่ละปืน เทียบ M416 = 1 (ค่าประมาณจากประสบการณ์ผู้เล่น ไม่ใช่ตัวเลขทางการของเกม)
        //   ตัวคูณปืน = ค่าของปืนที่ถือ / ค่าของปืนที่ใช้จูนตาราง (RefGun) × ที่ผู้ใช้ปรับเพิ่ม
        public static readonly Dictionary<string, double> BuiltinGun = new()
        {
            ["M416"] = 1.00, ["SCAR"] = 0.95, ["QBZ"] = 0.95, ["G36C"] = 1.00, ["AUG"] = 0.92, ["ACE32"] = 1.15,
            ["K2"] = 1.02, ["FAMAS"] = 1.10, ["M16A4"] = 0.90, ["BERYL"] = 1.35, ["AKM"] = 1.25, ["GROZA"] = 1.20,
            ["MK47"] = 1.20, ["UMP"] = 0.75, ["VECTOR"] = 0.70, ["MICRO"] = 0.60, ["UZI"] = 0.60, ["TOMMY"] = 0.80,
            ["MP5K"] = 0.75, ["PP19"] = 0.65, ["BIZON"] = 0.65, ["P90"] = 0.60, ["JS9"] = 0.65, ["MP9"] = 0.60,
            ["DP28"] = 0.85, ["M249"] = 0.90, ["MG3"] = 1.30, ["MINI14"] = 0.70, ["SKS"] = 1.00, ["SLR"] = 1.20,
            ["MK12"] = 0.80, ["QBU"] = 0.75, ["VSS"] = 0.60, ["MK14"] = 1.40, ["DRAGUNOV"] = 1.30,
        };

        public static double GunFactor(CalcSettings c, string gunKey)
        {
            double Of(string k) => k != null && BuiltinGun.TryGetValue(k, out double v) ? v : 1;
            double user = gunKey != null && c.Guns.TryGetValue(gunKey, out double u) ? u : 1;
            return Of(gunKey) / Of(c.RefGun ?? "AUG") * user;
        }

        public static string GunKey(string ocrName)
        {
            string n = new string((ocrName ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (n.Length < 2) return null;
            foreach (var g in ScopeDetect.GunWordList)
                if (n.StartsWith(g) || n.Replace('O', '0').StartsWith(g.Replace('O', '0'))) return g;
            return null;
        }

        // ---------------------------------------------------------------- ของแต่ง (แถวล่างของแผงปืน)
        // ช่อง: 0 ปากกระบอก, 1 กริป, 2 แม็ก (ไม่คิด), 3 พานท้าย — ห่างจากกล่องเลข (สัดส่วนความสูงจอ)
        static readonly double[] SlotDx = { 0.009, 0.104, 0.206, 0.308 };
        const double SlotDy = 0.140, SlotSize = 0.044;

        public static bool[] ReadAttachments(Settings cfg, Bitmap shot, Point origin, int w, int h, int panel, out string[] ids)
        {
            var b = panel == 0 ? cfg.ScopeBadge1 : cfg.ScopeBadge2;
            var has = new bool[4];
            ids = new string[4];
            for (int i = 0; i < 4; i++)
            {
                if (i == 2) continue;
                var r = ScopeDetect.Box(b[0] + SlotDx[i] * h / w, b[1] + SlotDy, SlotSize, w, h);
                r.Offset(-origin.X, -origin.Y);
                using var c = ScopeDetect.Crop(shot, r);
                has[i] = c.Width > 4 && ScopeDetect.Detail(c) >= 0.05;
                if (!has[i]) continue;
                // ชิ้นไหน: ลองเลื่อนกรอบ ±2 px เผื่อตำแหน่งเพี้ยนนิดหน่อย เอาที่ตรงสุด
                (AttachmentId.Item item, double score) best = (null, -1);
                int d = Math.Max(1, r.Width / 20);
                foreach (int ox in new[] { 0, -d, d })
                    foreach (int oy in new[] { 0, -d, d })
                    {
                        var rr = r;
                        rr.Offset(ox, oy);
                        using var cc = (ox | oy) == 0 ? null : ScopeDetect.Crop(shot, rr);
                        var m = AttachmentId.Identify(cc ?? c, i);
                        if (m.item != null && m.score > best.score) best = (m.item, m.score);
                    }
                ids[i] = best.item?.Id;
            }
            return has;
        }

        public static Rectangle AttachmentArea(Settings cfg, int w, int h)
        {
            var rects = new List<Rectangle>();
            foreach (var b in new[] { cfg.ScopeBadge1, cfg.ScopeBadge2 })
                for (int i = 0; i < 4; i++)
                    rects.Add(ScopeDetect.Box(b[0] + SlotDx[i] * h / w, b[1] + SlotDy, SlotSize, w, h));
            return rects.Aggregate(Rectangle.Union);
        }

        // ---------------------------------------------------------------- รวม
        // ของแต่งชิ้นนี้คูณแรงดึงเท่าไหร่: รู้ชนิด = ตัวเลขจากเกม, ติดแต่ไม่แน่ใจชนิด = ค่ากลาง (ตั้งได้)
        public static double AttFactor(bool has, string id, double defPct) =>
            !has ? 1 : AttachmentId.Get(id) is AttachmentId.Item it ? 1 - it.Vertical / 100 : 1 + defPct / 100;

        public static double DefPct(CalcSettings c, int slot) => slot == 0 ? c.MuzzlePct : slot == 1 ? c.GripPct : c.StockPct;

        public static (double mult, string why) Gear(CalcSettings c, string gunKey, double scopeMult, bool[] att, string[] ids = null)
        {
            double gun = GunFactor(c, gunKey);
            double gear = AttFactor(att?[0] == true, ids?[0], c.MuzzlePct) * AttFactor(att?[1] == true, ids?[1], c.GripPct)
                        * AttFactor(att?[3] == true, ids?[3], c.StockPct);
            double refGear = AttFactor(c.RefMuzzle, c.RefMuzzleId, c.MuzzlePct) * AttFactor(c.RefGrip, c.RefGripId, c.GripPct)
                           * AttFactor(c.RefStock, c.RefStockId, c.StockPct);
            double m = gun * scopeMult * gear / refGear;
            var parts = new List<string>();
            if (Math.Abs(gun - 1) > 1e-6) parts.Add($"ปืน ×{gun:0.00}");
            if (Math.Abs(scopeMult - 1) > 1e-6) parts.Add($"สโคป ×{scopeMult:0.00}");
            string A(int i, string none) => att[i] ? AttachmentId.Get(ids?[i])?.Id ?? "?" : none;
            if (att != null) parts.Add($"ปาก {A(0, "✗")} · กริป {A(1, "✗")}" + (att[3] ? $" · พาน {A(3, "")}" : ""));
            return (Math.Clamp(m, 0.2, 3.9), string.Join(" · ", parts));
        }
    }
}
