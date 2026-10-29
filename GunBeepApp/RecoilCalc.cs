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
        public double MuzzlePct { get; set; } = -15;
        public double GripPct { get; set; } = -15;
        public double StockPct { get; set; } = -8;
        public bool RefMuzzle { get; set; } = true;          // ตารางที่จูนไว้ จูนตอนติดปากกระบอก
        public bool RefGrip { get; set; } = true;            // ... ติดกริป
        public bool RefStock { get; set; } = false;          // ... ติดพานท้าย
        public double CrouchPct { get; set; } = -28;
        public double PronePct { get; set; } = -45;
        public Dictionary<string, double> Guns { get; set; } = new();   // ชื่อปืน → ตัวคูณ (1 = เท่าตาราง)
    }

    public static class RecoilCalc
    {
        // ---------------------------------------------------------------- สโคป
        public static readonly (string label, double mag, string sensName)[] Scopes =
        {
            ("reddot", 1, "Scoping"), ("holo", 1, "Scoping"), ("2x", 2, "Scope2X"), ("3x", 3, "Scope3X"),
            ("4x", 4, "Scope4X"), ("6x", 6, "Scope6X"), ("8x", 8, "Scope8X"), ("15x", 15, "Scope15X"),
        };

        static Dictionary<string, double> sens;
        static DateTime sensRead;

        // ความไวแต่ละสโคปจากไฟล์ตั้งค่า PUBG (ไฟล์บนดิสก์) — ไม่ได้เปิดใช้ความไวแยกสโคป = ทุกสโคปใช้ค่าเดียว
        public static Dictionary<string, double> Sens
        {
            get
            {
                if (sens != null && DateTime.Now - sensRead < TimeSpan.FromMinutes(1)) return sens;
                sensRead = DateTime.Now;
                var d = new Dictionary<string, double>();
                try
                {
                    string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                            "TslGame", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
                    string s;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var r = new StreamReader(fs)) s = r.ReadToEnd();
                    // เอาเฉพาะชุดของเมาส์ (Mouse, (Array=(...)))
                    int at = s.IndexOf("SensitiveMap=((Mouse", StringComparison.Ordinal);
                    string part = at >= 0 ? s.Substring(at, Math.Min(4000, s.Length - at)) : s;
                    foreach (Match m in Regex.Matches(part, @"SensitiveName=""(\w+)"",Sensitivity=([\d.]+)"))
                        d.TryAdd(m.Groups[1].Value, double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                    var per = Regex.Match(s, @"bIsUsingPerScopeMouseSensitivity=(\w+)");
                    if (per.Success && per.Groups[1].Value == "False" && d.TryGetValue("Scoping", out double one))
                        foreach (var sc in Scopes) d[sc.sensName] = one;
                }
                catch (Exception ex) { Log.Error("read pubg sens", ex); }
                return sens = d;
            }
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
            return sc.Value.mag / Conv(s);
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

        public static bool[] ReadAttachments(Settings cfg, Bitmap shot, Point origin, int w, int h, int panel)
        {
            var b = panel == 0 ? cfg.ScopeBadge1 : cfg.ScopeBadge2;
            var has = new bool[4];
            for (int i = 0; i < 4; i++)
            {
                if (i == 2) continue;
                var r = ScopeDetect.Box(b[0] + SlotDx[i] * h / w, b[1] + SlotDy, SlotSize, w, h);
                r.Offset(-origin.X, -origin.Y);
                using var c = ScopeDetect.Crop(shot, r);
                has[i] = c.Width > 4 && ScopeDetect.Detail(c) >= 0.05;
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
        public static (double mult, string why) Gear(CalcSettings c, string gunKey, double scopeMult, bool[] att)
        {
            double gun = gunKey != null && c.Guns.TryGetValue(gunKey, out double g) ? g : 1;
            double F(bool has, double pct) => has ? 1 + pct / 100 : 1;
            double gear = F(att?[0] == true, c.MuzzlePct) * F(att?[1] == true, c.GripPct) * F(att?[3] == true, c.StockPct);
            double refGear = F(c.RefMuzzle, c.MuzzlePct) * F(c.RefGrip, c.GripPct) * F(c.RefStock, c.StockPct);
            double m = gun * scopeMult * gear / refGear;
            var parts = new List<string>();
            if (Math.Abs(gun - 1) > 1e-6) parts.Add($"ปืน ×{gun:0.00}");
            if (Math.Abs(scopeMult - 1) > 1e-6) parts.Add($"สโคป ×{scopeMult:0.00}");
            if (att != null) parts.Add((att[0] ? "ปาก✓" : "ปาก✗") + (att[1] ? " กริป✓" : " กริป✗") + (att[3] ? " พาน✓" : ""));
            return (Math.Clamp(m, 0.2, 3.9), string.Join(" · ", parts));
        }
    }
}
