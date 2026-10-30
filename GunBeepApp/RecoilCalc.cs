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
        // เรียนรู้จากมือผู้ใช้ตอนยิงค้าง: ปืน → แรงคูณแต่ละช่วงเวลา (8 ช่วง × 0.5 วิ) และจำนวนชุดที่เรียนแล้ว
        public bool AutoLearn { get; set; }                     // ปิด = ค่าคำนวณล้วน (ค่าเริ่มต้น); เปิด = ปรับเองจากภาพในเกม
        public Dictionary<string, double[]> Learn { get; set; } = new();
        public Dictionary<string, int> LearnN { get; set; } = new();
        public Dictionary<string, double> LearnX { get; set; } = new();     // ปืน → ซ้าย/ขวาที่เรียนรู้ (+ = ดึงขวา)
        public Dictionary<string, double> LearnKick { get; set; } = new();  // ปืนเคาะ → แรงต่อนัดที่เรียนรู้ (คูณ)
        public Dictionary<string, double> LearnScope { get; set; } = new(); // สโคป → แรงดึงที่เรียนรู้ของสโคปนั้น (คูณ) แยกทุกสโคป
        public Dictionary<string, double> ViewK { get; set; } = new();      // สโคป → px ต่อ count ที่วัดได้ (ตัววัดภาพ)
        public bool LearnLocked { get; set; }                               // ล็อกค่า: ใช้ค่าที่เรียนแล้วต่อ แต่ไม่ปรับเพิ่ม
    }

    public static class RecoilCalc
    {
        // ---------------------------------------------------------------- สโคป
        public static readonly (string label, double mag, string sensName)[] Scopes =
        {
            ("reddot", 1, "Scoping"), ("holo", 1, "Scoping"), ("2x", 2, "Scope2X"), ("3x", 3, "Scope3X"),
            ("4x", 4, "Scope4X"), ("hybrid", 1, "Scoping"),     // ไฮบริด = ใช้แบบเรดดอท (ผู้ใช้บอก) ("6x", 6, "Scope6X"), ("8x", 8, "Scope8X"), ("15x", 15, "Scope15X"),
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
        //   2026-10-30: ปืนแรง (Beryl/AKM/…) ลดลง ~12% ตามที่ระบบเรียนรู้เห็นจากมือผู้ใช้ (ดันเมาส์ขึ้นทุกชุด)
        public static readonly Dictionary<string, double> BuiltinGun = new()
        {
            ["M416"] = 1.00, ["SCAR"] = 0.95, ["QBZ"] = 0.95, ["G36C"] = 1.00, ["AUG"] = 0.92, ["ACE32"] = 1.08,
            ["K2"] = 1.02, ["FAMAS"] = 1.05, ["M16A4"] = 0.90, ["BERYL"] = 1.20, ["AKM"] = 1.10, ["GROZA"] = 1.10,
            ["MK47"] = 1.10, ["UMP"] = 0.75, ["VECTOR"] = 0.70, ["MICRO"] = 0.60, ["UZI"] = 0.60, ["TOMMY"] = 0.80,
            ["MP5K"] = 0.75, ["PP19"] = 0.65, ["BIZON"] = 0.65, ["P90"] = 0.60, ["JS9"] = 0.65, ["MP9"] = 0.60,
            ["DP28"] = 0.75, ["RPD"] = 0.75, ["M249"] = 0.75, ["MG3"] = 0.75, ["MINI14"] = 0.70, ["SKS"] = 1.00, ["SLR"] = 1.12,
            ["MK12"] = 0.80, ["QBU"] = 0.75, ["VSS"] = 0.60, ["MK14"] = 1.30, ["DRAGUNOV"] = 1.20,
        };

        // แม็กปกติ, แม็กขยาย, เวลาต่อนัด (ms) ตอนยิงออโต้ — ค่าที่ผู้เล่นรู้กันทั่วไป (ไม่ใช่ข้อมูลทางการ อาจต่างตามแพตช์)
        //   2026-10-30: แม็กปกติ + เวลาต่อนัด เทียบกับป้ายปืนในเกมที่ผู้ใช้แคป (แถบอัตรายิง ≈ นัดต่อนาที ÷ 1000; AKM 0.60, M416 0.70, FAMAS 0.90)
        public static readonly Dictionary<string, (int mag, int ext, double ms)> GunInfo = new()
        {
            ["M416"] = (30, 40, 86), ["SCAR"] = (30, 40, 93), ["QBZ"] = (30, 40, 93), ["G36C"] = (30, 40, 86),
            ["AUG"] = (30, 40, 84), ["ACE32"] = (30, 40, 88), ["K2"] = (30, 40, 86), ["FAMAS"] = (30, 35, 67),
            ["M16A4"] = (30, 40, 75), ["BERYL"] = (30, 40, 86), ["AKM"] = (30, 40, 100), ["GROZA"] = (30, 40, 80),
            ["MK47"] = (20, 30, 100), ["UMP"] = (25, 35, 90), ["VECTOR"] = (19, 33, 55), ["MICRO"] = (25, 35, 48),
            ["UZI"] = (25, 35, 48), ["TOMMY"] = (30, 50, 80), ["MP5K"] = (30, 40, 67), ["PP19"] = (53, 53, 86),
            ["BIZON"] = (53, 53, 86), ["P90"] = (50, 50, 60), ["JS9"] = (30, 40, 67), ["MP9"] = (30, 40, 60),
            ["DP28"] = (47, 47, 109), ["RPD"] = (50, 50, 80), ["M249"] = (75, 100, 75), ["MG3"] = (75, 100, 91), ["MINI14"] = (20, 30, 100),
            ["SKS"] = (10, 20, 100), ["SLR"] = (10, 20, 100), ["MK12"] = (20, 30, 100), ["QBU"] = (10, 20, 100),
            ["VSS"] = (10, 20, 86), ["MK14"] = (10, 20, 90), ["DRAGUNOV"] = (10, 20, 100),
        };

        // ปืนเคาะ (ยิงทีละนัด): ดึงลงหนึ่งจังหวะต่อคลิก แรงต่อนัด = แรงต่อนัดของตาราง AUG × ค่านี้ (ประมาณ, เรียนรู้ต่อเอง)
        public static readonly Dictionary<string, double> SemiKick = new()
        {
            ["MINI14"] = 0.95, ["MK12"] = 1.05, ["QBU"] = 1.05, ["SKS"] = 1.45, ["SLR"] = 1.7, ["DRAGUNOV"] = 1.7,
            ["M16A4"] = 0.75, ["MK47"] = 0.95,
        };

        // โหมดยิงของแต่ละปืน (จากป้ายปืนในเกม): S ทีละนัด, B ยิงชุด, A ออโต้, F ออโต้เร็ว (MG3) — ปุ่ม B ในเกมวนตามลำดับนี้
        public static readonly Dictionary<string, string> FireModes = new()
        {
            ["AKM"] = "SA", ["BERYL"] = "SBA", ["G36C"] = "SA", ["M416"] = "SA", ["M16A4"] = "SB", ["SCAR"] = "SA", ["MK47"] = "SB",
            ["QBZ"] = "SA", ["AUG"] = "SA", ["GROZA"] = "SA", ["K2"] = "SBA", ["ACE32"] = "SA", ["FAMAS"] = "SBA",
            ["TOMMY"] = "SA", ["UMP"] = "SBA", ["MICRO"] = "SA", ["VECTOR"] = "SBA", ["MP5K"] = "SBA", ["P90"] = "SA", ["MP9"] = "SA", ["JS9"] = "SA",
            ["VSS"] = "SA", ["MK14"] = "SA", ["M249"] = "A", ["MG3"] = "AF", ["RPD"] = "A", ["DP28"] = "A",
            ["MINI14"] = "S", ["SKS"] = "S", ["SLR"] = "S", ["MK12"] = "S", ["QBU"] = "S", ["DRAGUNOV"] = "S",
        };

        // ยิงชุดออกกี่นัดต่อคลิก (ค่าที่ผู้เล่นรู้กัน ไม่ใช่ข้อมูลทางการ)
        public static readonly Dictionary<string, int> BurstN = new()
        {
            ["M16A4"] = 3, ["BERYL"] = 3, ["K2"] = 3, ["FAMAS"] = 3, ["MK47"] = 2, ["UMP"] = 2, ["VECTOR"] = 2, ["MP5K"] = 3,
        };

        // ปืนออโต้ที่สลับเป็นทีละนัด/ยิงชุด: แรงต่อนัดเทียบนัดหนึ่งของตาราง (ไม่มีแรงสะสมเหมือนยิงค้าง)
        public const double SingleKick = 0.65;
        public static double KickOf(string gk) => gk != null && SemiKick.TryGetValue(gk, out double v) ? v : SingleKick;

        // ปืนที่ไม่ดึงเลย: สไนเปอร์ขึ้นลำ / ลูกซอง / ยิงระเบิด / หน้าไม้ (ยิงทีละนัดแล้วต้องเล็งใหม่ ดึงลง = เป้าหลุด)
        public static readonly HashSet<string> NoPull = new()
        {
            "KAR98K", "M24", "AWM", "MOSIN", "LYNX", "WIN94", "S12K", "S1897", "S686", "DBS", "O12", "SAWED",
            "CROSSBOW", "PANZERFAUST", "M79", "MGL",
        };

        public static double GunFactor(CalcSettings c, string gunKey)
        {
            double Of(string k) => k != null && BuiltinGun.TryGetValue(k, out double v) ? v : 1;
            double user = gunKey != null && c.Guns.TryGetValue(gunKey, out double u) ? u : 1;
            return Of(gunKey) / Of(c.RefGun ?? "AUG") * user;
        }

        // ชื่อปืนในเกม (key ตรงกับที่ใช้เก็บค่าเรียนรู้ / ตารางปืน) + ชื่ออื่นที่ OCR อาจอ่านได้
        public static readonly (string key, string name, string[] alias)[] GunNames =
        {
            ("AKM", "AKM", null), ("M416", "M416", null), ("BERYL", "Beryl M762", new[] { "Beryl" }), ("MK12", "Mk12", null),
            ("SCAR", "SCAR-L", new[] { "SCAR" }), ("AUG", "AUG", null), ("ACE32", "ACE32", null), ("QBZ", "QBZ", new[] { "QBZ95" }),
            ("G36C", "G36C", null), ("K2", "K2", null), ("FAMAS", "FAMAS", null), ("M16A4", "M16A4", null), ("GROZA", "Groza", null),
            ("MK47", "Mk47 Mutant", new[] { "Mk47" }), ("SLR", "SLR", null), ("SKS", "SKS", null), ("MINI14", "Mini14", null),
            ("MK14", "Mk14", new[] { "Mk14 EBR" }), ("QBU", "QBU", null), ("VSS", "VSS", null), ("UMP", "UMP45", new[] { "UMP" }),
            ("VECTOR", "Vector", null), ("MICRO", "Micro UZI", new[] { "UZI", "Micro" }), ("TOMMY", "Tommy Gun", new[] { "Tommy" }),
            ("MP5K", "MP5K", null), ("PP19", "PP-19 Bizon", new[] { "Bizon", "PP19" }), ("P90", "P90", null), ("JS9", "JS9", null),
            ("MP9", "MP9", null), ("DP28", "DP-28", null), ("RPD", "RPD", null), ("M249", "M249", null), ("MG3", "MG3", null), ("DRAGUNOV", "Dragunov", null),
            ("KAR98K", "Kar98k", null), ("M24", "M24", null), ("AWM", "AWM", null), ("MOSIN", "Mosin Nagant", new[] { "Mosin" }),
            ("LYNX", "Lynx AMR", new[] { "Lynx" }), ("WIN94", "Win94", null), ("S12K", "S12K", null), ("S1897", "S1897", null),
            ("S686", "S686", null), ("DBS", "DBS", null), ("O12", "O12", null), ("SAWED", "Sawed-off", new[] { "Sawed" }),
            ("P92", "P92", null), ("P1911", "P1911", null), ("P18C", "P18C", null), ("R1895", "R1895", null), ("R45", "R45", null),
            ("DEAGLE", "Deagle", null), ("SKORPION", "Skorpion", null), ("CROSSBOW", "Crossbow", null),
            ("PANZERFAUST", "Panzerfaust", null), ("M79", "M79", null), ("MGL", "MGL", null),
        };

        // ตัวที่ OCR ชอบสลับกัน → ทำให้เหมือนกันทั้งสองฝั่งก่อนเทียบ
        static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in (s ?? "").ToUpperInvariant())
            {
                if (!char.IsLetterOrDigit(ch) || ch > 127) continue;
                sb.Append(ch switch { 'O' => '0', 'I' or 'L' => '1', 'G' => '6', 'S' => '5', 'B' => '8', 'Z' => '2', _ => ch });
            }
            return sb.ToString();
        }

        static string[] Words(string s) =>
            (s ?? "").Split(new[] { ' ', '-', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(Norm).Where(w => w.Length > 0).ToArray();

        static int Lev(string a, string b)
        {
            var d = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) d[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int prev = d[0];
                d[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int t = d[j];
                    d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                    prev = t;
                }
            }
            return d[b.Length];
        }

        static double Sim(string a, string b) => a.Length == 0 || b.Length == 0 ? 0 : 1 - (double)Lev(a, b) / Math.Max(a.Length, b.Length);

        static readonly Dictionary<string, (string key, string name)> gunCache = new();

        // OCR ชื่อบนแผงปืน → ปืนจริง: เทียบทั้งคำ (คำแรก หรือสองคำแรก) กับทุกชื่อ เอาที่เหมือนสุด
        //   ต้องเหมือน ≥ 75% (ชื่อ ≤ 3 ตัวต้องตรงเป๊ะ) และชนะอันดับสองชัด — ไม่งั้น = อ่านไม่ออก (ใช้ชื่อเดิม)
        //   เดิมใช้ "ขึ้นต้นด้วย" → "Mk147 Mutant" กลายเป็น Mk14, ตัวหนังสือมั่วก็โชว์ตามที่อ่าน
        public static (string key, string name) MatchGun(string ocr)
        {
            if (string.IsNullOrWhiteSpace(ocr)) return (null, null);
            lock (gunCache) if (gunCache.TryGetValue(ocr, out var hit)) return hit;
            var w = Words(ocr);
            (string key, string name) best = (null, null);
            double bs = 0, second = 0;
            if (w.Length > 0)
                foreach (var g in GunNames)
                {
                    double s = 0;
                    foreach (var alias in new[] { g.name }.Concat(g.alias ?? Array.Empty<string>()))
                    {
                        var aw = Words(alias);
                        string aj = string.Concat(aw);
                        double v = Sim(w[0], aj);                                                   // คำเดียว (หรืออ่านติดกัน)
                        if (w.Length >= 2) v = Math.Max(v, Sim(w[0] + w[1], aj));                   // สองคำแรก
                        if (aj.Length <= 3 && v < 1) v = 0;                                         // ชื่อสั้นต้องตรงเป๊ะ
                        s = Math.Max(s, v);
                    }
                    if (s > bs) { if (best.key != g.key) second = Math.Max(second, bs); best = (g.key, g.name); bs = s; }
                    else second = Math.Max(second, s);
                }
            var res = bs >= 0.75 && bs - second >= 0.08 ? best : (null, null);
            lock (gunCache) { if (gunCache.Count > 500) gunCache.Clear(); gunCache[ocr] = res; }
            return res;
        }

        public static string GunKey(string name) => MatchGun(name).key;

        // ---------------------------------------------------------------- ของแต่ง (แถวล่างของแผงปืน)
        // ช่อง: 0 ปากกระบอก, 1 กริป, 2 แม็ก (ไม่คิด), 3 พานท้าย — ห่างจากกล่องเลข (สัดส่วนความสูงจอ)
        static readonly double[] SlotDx = { 0.009, 0.104, 0.206, 0.308 };
        const double SlotDy = 0.140, SlotSize = 0.044;

        public static bool[] ReadAttachments(Settings cfg, Bitmap shot, Point origin, int w, int h, int panel, out string[] ids, out bool[] unsure)
        {
            var b = panel == 0 ? cfg.ScopeBadge1 : cfg.ScopeBadge2;
            var has = new bool[4];
            ids = new string[4];
            unsure = new bool[4];
            for (int i = 0; i < 4; i++)
            {
                var r = SlotRect(cfg, w, h, panel, i);
                r.Offset(-origin.X, -origin.Y);
                using var c = ScopeDetect.Crop(shot, r);
                // เมาส์ชี้ช่องนี้อยู่ (พื้นขาว) หรือป้ายรายละเอียดของบังอยู่ (เทาอ่อน) → อ่านไม่ได้ ใช้ค่าเดิม
                if (c.Width > 4 && (BrightShare(c) > 0.4 || ScopeDetect.Obscured(c))) { unsure[i] = true; continue; }
                has[i] = c.Width > 4 && ScopeDetect.Detail(c) >= 0.05;
                // ช่องว่างที่เห็นพื้นหลังเป็นลาย (ดิน/ฟ้า) เบลอ → ความคม 1–7; ไอคอนจริงคม 19–33 (วัดจากภาพจริง 639 ช่อง)
                if (has[i] && i != 2 && Sharpness(c) < 12) has[i] = false;
                if (i == 2 && has[i])
                {
                    // รูปแม็กของตัวปืน (AKM, Beryl, Mk47 …) ยื่นลงมาทับครึ่งบนของช่อง → เดิมอ่านว่าใส่แม็กขยาย (นับ 40 แทน 30)
                    //   ไอคอนแม็กจริงวางเฉียงกลางช่อง ลงมาถึงครึ่งล่าง: ครึ่งล่างต้องมีลาย (รูปปืนทับ: sd ≤ 10 จากภาพจริง)
                    has[i] = ScopeDetect.BandSd(c, 0.56, 0.94) >= 15;
                    // QuickDraw อย่างเดียว (บรรจุไว ไม่เพิ่มนัด) ไอคอนสีฟ้า; แม็กขยาย / ขยาย+QuickDraw สีเทาเข้ม (วัดจากไอคอนในเกม)
                    if (has[i]) ids[i] = ScopeDetect.TealShare(c) >= 0.03 ? "quickdraw" : "extended";
                }
                if (!has[i] || i == 2) continue;          // ช่องแม็ก: รู้แค่ขยาย / QuickDraw
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
                // ปืนที่ไม่มีช่องพานท้าย (AUG, Beryl …) ตำแหน่งนี้ทับรูปแม็กของปืน → นับว่ามีพานท้ายเฉพาะตอนรู้จักว่าเป็นพานท้ายจริง
                if (i == 3 && ids[i] == null) has[i] = false;
                // ปาก/กริป: คมแต่จำไม่ได้ (ต้นไม้/รั้วข้างหลัง, รูปปืนทับ, ของที่ยังไม่มีต้นแบบ) → ไม่แน่ใจ ใช้ค่าเดิม แทนการเดาว่า "ติด"
                if (i != 3 && ids[i] == null) { has[i] = false; unsure[i] = true; }
            }
            return has;
        }

        // ความคม: ค่าเฉลี่ย |ความสว่างต่างกับพิกเซลขวา/ล่าง| เว้นขอบ 10% (พื้นหลังในหน้ากระเป๋าเบลอ ไอคอนคม)
        public static double Sharpness(Bitmap c)
        {
            double s = 0; int n = 0;
            int m = Math.Max(2, c.Width / 10);
            for (int y = m; y < c.Height - m; y++)
                for (int x = m; x < c.Width - m - 1; x++)
                {
                    Color a = c.GetPixel(x, y), b = c.GetPixel(x + 1, y), d = c.GetPixel(x, y + 1);
                    double la = 0.3 * a.R + 0.59 * a.G + 0.11 * a.B;
                    s += Math.Abs(la - (0.3 * b.R + 0.59 * b.G + 0.11 * b.B)) + Math.Abs(la - (0.3 * d.R + 0.59 * d.G + 0.11 * d.B));
                    n++;
                }
            return n == 0 ? 0 : s / n;
        }

        static double BrightShare(Bitmap c)
        {
            int n = 0, hi = 0;
            for (int y = 0; y < c.Height; y += 2)
                for (int x = 0; x < c.Width; x += 2)
                {
                    var px = c.GetPixel(x, y);
                    n++;
                    if (0.3 * px.R + 0.59 * px.G + 0.11 * px.B > 200) hi++;
                }
            return n == 0 ? 0 : (double)hi / n;
        }

        public static Rectangle SlotRect(Settings cfg, int w, int h, int panel, int i)
        {
            var b = panel == 0 ? cfg.ScopeBadge1 : cfg.ScopeBadge2;
            return ScopeDetect.Box(b[0] + SlotDx[i] * h / w, b[1] + SlotDy, SlotSize, w, h);
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
