using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace GunBeep
{
    // อัดค่าแรงดึงอัตโนมัติ
    //   ปิดมาโคร: ยิงค้างใส่กำแพงแล้วลากเมาส์ลงกดเป้าให้นิ่งเอง 3-5 ชุด → อัดการดึงทั้งหมดของมือ
    //   เปิดมาโคร (จูนละเอียด): ยิงแล้วดึงแก้เฉพาะส่วนที่มาโครยังขาด/เกิน → แรงดึงจริง = ที่มาโครดึง + ที่มือแก้
    //   GunBeep อัดการเคลื่อนเมาส์จริง (Raw Input) ทุก 10 ms ตั้งแต่กดยิงจนปล่อย
    //   เฉลี่ยทุกชุด → base (แรงดึงต่ำสุด) + กราฟ (ส่วนที่เพิ่มตามเวลา) + x (ดึงซ้าย/ขวาเฉลี่ย)
    public class Calibrator : Window
    {
        public class Spray
        {
            public double[] Y, X;           // แรงดึงทั้งหมดที่ต้องใช้ ต่อ 10 ms
            public double Ms;
            public bool Tuned;              // อัดตอนเปิดมาโคร
            public double Corr, CorrX;      // มือดึงแก้เฉลี่ยต่อ 10 ms (+ = ต้องดึงลงเพิ่ม)
            public double CorrRms;          // ความแกว่งของการดึงแก้ (ต่อแท่ง 100 ms)
            public double Ratio = double.NaN;   // การขยับของมาโครที่วัดได้ / ที่ตั้งไว้ (เช็กหน่วย)
        }

        public class Fit
        {
            public double Base, X;
            public double[] Steps;          // กราฟต่อ 10 ms (ปัดเป็นแท่ง 100 ms แล้ว)
            public double[] Avg;            // แรงดึงเฉลี่ยจริงต่อ 10 ms (ไว้วาด)
            public int Used, Dropped;
        }

        const double MIN_MS = 500;          // ยิงสั้นกว่านี้ไม่นับ (แตะยิง)
        const double PW = 640, PH = 220, PADL = 40, PADB = 24, PADT = 10, PADR = 8;

        static readonly Color Blue = Color.FromRgb(0x1D, 0x6B, 0xFF), Ice = Color.FromRgb(0x5C, 0xE1, 0xFF), Gold = Color.FromRgb(0xC9, 0xA5, 0x4A);
        static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFF));
        static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x80, 0x94, 0xBC));
        static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x22, 0x3A));

        readonly string[] names;
        readonly Func<int> macroGun;                    // ปืนที่มาโครใช้อยู่ (1-3), 0 = ปิดมาโคร
        readonly Func<int, GHub.GunVals> inMacro;       // ค่าที่สคริปต์ใน G Hub ใช้อยู่จริงของปืนนั้น
        readonly Action<int, Fit> apply;
        readonly Action<string> pop;
        readonly Style keyBtn;
        readonly Button[] tabs = new Button[3];
        readonly Button applyBtn, undoBtn, autoBtn;
        readonly TextBlock status = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4), TextWrapping = TextWrapping.Wrap };
        readonly TextBlock result = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        readonly Canvas plot = new Canvas { Width = PW, Height = PH, ClipToBounds = true };
        readonly List<Spray> sprays = new();
        readonly Stopwatch clock = Stopwatch.StartNew();

        RawMouse raw;
        int gun, round = 1;
        bool down, ignoreThis;
        double t0;
        IntPtr hand;                                    // เมาส์ที่กดยิง = มือคน
        GHub.GunVals macroVals;                         // ค่าที่มาโครใช้ในชุดนี้ (null = ปิดมาโคร)
        List<(double t, int dx, int dy, bool hand)> cur;

        public Calibrator(string[] gunNames, int startGun, Func<int> activeGun, Func<int, GHub.GunVals> macroValues,
                          Action<int, Fit> onApply, Action<string> onPop)
        {
            names = gunNames;
            gun = Math.Clamp(startGun - 1, 0, names.Length - 1);
            macroGun = activeGun;
            inMacro = macroValues;
            apply = onApply;
            pop = onPop;
            keyBtn = Application.Current.MainWindow?.TryFindResource("KeyBtn") as Style;

            Title = "อัดค่าแรงดึง";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI, Leelawadee UI");

            var title = new TextBlock { Text = "อัดค่าแรงดึงอัตโนมัติ", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Text };
            var tabRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < names.Length; i++)
            {
                int g = i;
                tabs[i] = Btn("", () => { if (g != gun) { gun = g; sprays.Clear(); Refresh(); } }, 6);
                tabRow.Children.Add(tabs[i]);
            }
            var head = new DockPanel();
            DockPanel.SetDock(tabRow, Dock.Right);
            head.Children.Add(tabRow);
            head.Children.Add(title);

            var steps = new TextBlock
            {
                FontSize = 12.5, Foreground = Muted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = PW + 16,
                Text = "เข้าห้องซ้อม ถือปืน + กล้องที่จะใช้จริง เล็งจุดหนึ่งบนกำแพง ยิงค้างจนเกือบหมดแม็ก ให้เป้าอยู่จุดเดิมให้มากที่สุด\n" +
                       "• ครั้งแรก (ปิดมาโคร): ลากเมาส์ลงเองทั้งหมด 3–5 ชุด แล้วกด \"ใช้ค่านี้\"\n" +
                       "• จูนละเอียด (เปิดมาโคร ปืนเดียวกับแท็บนี้): ยิงแล้วดึงแก้เฉพาะส่วนที่มาโครยังขาด/เกิน 3 ชุด → \"ใช้ค่านี้\" → " +
                       "ทำซ้ำจนขึ้นว่า \"นิ่งแล้ว\" — ค่าจะเข้ากับมือ เมาส์ และ sens ของคุณเอง\n" +
                       "• ออโต้จูน: กด ▶ ออโต้จูน แล้วเปิดมาโครยิงไปเรื่อยๆ (ดึงแก้เฉพาะส่วนที่เป้าขยับ) — ทุก 3 ชุดปรับค่าเอง หยุดเองเมื่อนิ่ง"
            };

            undoBtn = Btn("ลบชุดล่าสุด", () => { if (sprays.Count > 0) sprays.RemoveAt(sprays.Count - 1); Refresh(); }, 0);
            var clearBtn = Btn("เริ่มใหม่", () => { sprays.Clear(); Refresh(); }, 6);
            applyBtn = Btn("ใช้ค่านี้", Apply, 8);
            applyBtn.BorderBrush = new SolidColorBrush(Blue);
            autoBtn = Btn("", ToggleAuto, 6);
            autoBtn.BorderBrush = new SolidColorBrush(Gold);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(undoBtn);
            left.Children.Add(clearBtn);
            left.Children.Add(autoBtn);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(Btn("ปิด", Close, 0));
            right.Children.Add(applyBtn);
            var foot = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(right, Dock.Right);
            foot.Children.Add(right);
            foot.Children.Add(left);

            var plotBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x16)), CornerRadius = new CornerRadius(12),
                BorderBrush = GridBrush, BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = plot
            };

            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(steps);
            stack.Children.Add(status);
            stack.Children.Add(plotBox);
            stack.Children.Add(result);
            stack.Children.Add(foot);

            Content = new Border
            {
                Margin = new Thickness(18), Padding = new Thickness(22, 18, 22, 18), CornerRadius = new CornerRadius(20),
                Background = new LinearGradientBrush(Color.FromRgb(0x0E, 0x15, 0x28), Color.FromRgb(0x07, 0x0A, 0x12), 90),
                BorderBrush = new LinearGradientBrush(Ice, Gold, 45), BorderThickness = new Thickness(1.5),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.6 },
                Child = stack
            };
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

            Loaded += (s, e) =>
            {
                try
                {
                    raw = new RawMouse();
                    raw.Input += OnRaw;
                }
                catch (Exception ex) { status.Text = "อ่านเมาส์ไม่ได้: " + ex.Message; }
            };
            Closed += (s, e) => raw?.Dispose();
            Refresh();
        }

        Button Btn(string label, Action click, double leftMargin)
        {
            var b = new Button { Content = label, Margin = new Thickness(leftMargin, 0, 0, 0) };
            if (keyBtn != null) b.Style = keyBtn;
            b.Click += (s, e) => click();
            return b;
        }

        // ---------------------------------------------------------------- อัด

        void OnRaw(IntPtr dev, int dx, int dy, int left)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (left == 1 && !down)
            {
                down = true;
                t0 = now;
                hand = dev;
                cur = new List<(double, int, int, bool)>();
                macroVals = null;
                ignoreThis = false;
                int mg = macroGun();
                if (auto && mg != 0 && mg - 1 != gun && mg - 1 < names.Length)
                {
                    gun = mg - 1;                               // ออโต้จูนตามปืนที่มาโครใช้อยู่
                    sprays.Clear();
                    goodRounds = autoRounds = 0;
                    Refresh();
                }
                if (mg != 0)
                {
                    macroVals = mg - 1 == gun ? inMacro(gun) : null;
                    ignoreThis = macroVals == null;
                    if (mg - 1 != gun) pop($"มาโครใช้ปืน {mg} อยู่ — สลับเป็น {names[gun]} ก่อน (ชุดนี้ไม่นับ)");
                    else if (ignoreThis) pop("รอส่งค่าเข้า G Hub ให้เสร็จก่อน (ชุดนี้ไม่นับ)");
                }
            }
            if (down && (dx != 0 || dy != 0)) cur.Add((now - t0, dx, dy, dev == hand));
            if (left == -1 && down && dev == hand)
            {
                down = false;
                double ms = now - t0;
                if (ignoreThis) return;
                if (ms < MIN_MS) return;                        // แตะยิง / คลิกทั่วไป
                if (auto && macroVals == null) { pop("ออโต้จูน: เปิดมาโครก่อนยิง (ชุดนี้ไม่นับ)"); return; }
                var s = macroVals == null ? Bucket(cur, ms) : Tune(cur, ms, macroVals);
                sprays.Add(s);
                if (macroVals != null) lastUsed = macroVals;
                Log.Info($"calib {names[gun]} #{sprays.Count} {ms:0}ms" + (s.Tuned
                    ? $" tuned corr {s.Corr:0.00} x {s.CorrX:0.00} rms {s.CorrRms:0.00} ratio {s.Ratio:0.00}" : ""));
                var f = Analyze(sprays);
                string fit = f != null ? $" · base {GHub.Fmt(f.Base)} x {GHub.Fmt(f.X)}" : "";
                if (auto)
                {
                    if (sprays.Count < AUTO_N) pop($"ออโต้จูน {sprays.Count}/{AUTO_N} · {Verdict(s.Corr, s.CorrRms).short_}");
                    Refresh();
                    AutoStep();
                    return;
                }
                pop(s.Tuned ? $"จูนชุดที่ {sprays.Count} · {Verdict(s.Corr, s.CorrRms).short_}{fit}"
                            : $"อัดชุดที่ {sprays.Count} ({ms / 1000:0.0} วิ){fit}");
                Refresh();
            }
        }

        static (double[] y, double[] x) Sum(IEnumerable<(double t, int dx, int dy, bool hand)> s, int n)
        {
            var y = new double[n];
            var x = new double[n];
            foreach (var (t, dx, dy, _) in s)
            {
                int k = (int)(t / 10);
                if (k >= n) continue;
                y[k] += dy;
                x[k] += dx;
            }
            return (y, x);
        }

        static int Len(double ms) => Math.Max(1, (int)(ms / 10));    // ตัดช่วงสุดท้ายที่ไม่เต็ม 10 ms

        static Spray Bucket(List<(double t, int dx, int dy, bool hand)> s, double ms)
        {
            var (y, x) = Sum(s, Len(ms));
            return new Spray { Y = y, X = x, Ms = ms };
        }

        // ชุดที่ยิงตอนเปิดมาโคร: แรงดึงที่ต้องใช้จริง = ที่มาโครดึง (ตามค่าที่ตั้ง) + ที่มือดึงแก้
        //   ถ้า Windows แยกการขยับของมาโครเป็นอีกอุปกรณ์ → ส่วนของเมาส์ที่กดยิง = มือล้วน
        //   ถ้ารวมมาในอุปกรณ์เดียวกัน (การขยับของอุปกรณ์อื่นแทบไม่มี) → มือ = ที่อัดได้ − ที่มาโครดึง
        static Spray Tune(List<(double t, int dx, int dy, bool hand)> s, double ms, GHub.GunVals v)
        {
            int n = Len(ms);
            var (hy, hx) = Sum(s.Where(e => e.hand), n);
            var (oy, _) = Sum(s.Where(e => !e.hand), n);
            var ey = new double[n];
            for (int k = 0; k < n; k++) ey[k] = v.Base + (v.Steps.Length == 0 ? 0 : v.Steps[Math.Min(k, v.Steps.Length - 1)]);
            double expSum = ey.Sum(), otherSum = oy.Sum();
            bool merged = expSum <= 0 || otherSum < expSum * 0.4;
            // วัดได้ว่ามาโครขยับจริงกี่เท่าของค่าที่ตั้ง → แปลงที่มือแก้ (หน่วยเมาส์) เป็นหน่วยของสคริปต์
            double ratio = merged ? double.NaN : otherSum / expSum;
            double unit = double.IsNaN(ratio) || ratio < 0.3 || ratio > 3 ? 1 : ratio;

            var y = new double[n];
            var x = new double[n];
            var cy = new double[n];
            for (int k = 0; k < n; k++)
            {
                cy[k] = (merged ? hy[k] - ey[k] : hy[k]) / unit;
                double cx = (merged ? hx[k] - v.X : hx[k]) / unit;
                y[k] = ey[k] + cy[k];
                x[k] = v.X + cx;
            }
            var bars = Bars(cy);
            return new Spray
            {
                Y = y, X = x, Ms = ms, Tuned = true,
                Corr = cy.Average(), CorrX = x.Average() - v.X,
                CorrRms = Math.Sqrt(bars.Average(b => b * b)),
                Ratio = ratio,
            };
        }

        // ดึงแก้เองน้อยแค่ไหน = เป้านิ่งแค่ไหน (หน่วยเดียวกับ base: ต่อ 10 ms)
        static (string text, string short_, bool good) Verdict(double corr, double rms)
        {
            if (Math.Abs(corr) < 0.3 && rms < 0.75) return ("นิ่งแล้ว ✓ แทบไม่ต้องดึงแก้", "นิ่งแล้ว ✓", true);
            string dir = corr > 0 ? "มาโครดึงไม่พอ" : "มาโครดึงเกิน";
            return Math.Abs(corr) >= 0.3 ? ($"{dir} (มือแก้เฉลี่ย {Math.Abs(corr):0.0}/10ms)", dir, false)
                                         : ("เฉลี่ยพอดีแต่ยังแกว่งตามช่วงเวลา (กราฟยังไม่ตรง)", "กราฟยังไม่ตรง", false);
        }

        // เฉลี่ยทุกชุด → base / x / กราฟ
        public static Fit Analyze(List<Spray> all)
        {
            if (all.Count == 0) return null;
            var use = all.ToList();
            int dropped = 0;
            if (use.Count >= 3)
            {
                // ตัดชุดที่แรงดึงรวมช่วงต้นต่างจากค่ากลางเกิน 40%
                int common = use.Min(s => s.Y.Length);
                var totals = use.Select(s => s.Y.Take(common).Sum()).ToList();
                double med = totals.OrderBy(v => v).ElementAt(totals.Count / 2);
                if (med > 0)
                {
                    var keep = use.Where((s, i) => Math.Abs(totals[i] - med) <= med * 0.4).ToList();
                    if (keep.Count >= 2) { dropped = use.Count - keep.Count; use = keep; }
                }
            }

            int need = Math.Max(1, (use.Count + 1) / 2);         // ช่วงเวลาที่มีอย่างน้อยครึ่งหนึ่งของชุด
            int len = 0;
            while (use.Count(s => s.Y.Length > len) >= need) len++;
            if (len < 10) return null;

            var avg = new double[len];
            double sumX = 0;
            int cntX = 0;
            for (int k = 0; k < len; k++)
            {
                var cov = use.Where(s => s.Y.Length > k).ToList();
                avg[k] = cov.Average(s => s.Y[k]);
                foreach (var s in cov) { sumX += s.X[k]; cntX++; }
            }

            var f = FromTotal(avg, cntX > 0 ? sumX / cntX : 0);
            f.Used = use.Count;
            f.Dropped = dropped;
            return f;
        }

        // แรงดึงรวมต่อ 10 ms → base (ส่วนต่ำสุด) + กราฟ (ส่วนที่เกิน) + x
        static Fit FromTotal(double[] total, double x)
        {
            var bars = LiveLink.Quantize(total);
            double min = bars.Min(), max = bars.Max();
            double baseV = Math.Max(0, Math.Floor(min * 2) / 2);
            if (max - baseV > 31.75) baseV = Math.Ceiling((max - 31.75) * 2) / 2;   // กราฟส่งได้สูงสุด 31.75
            var steps = LiveLink.Fit(bars.Select(v => Math.Clamp(v - baseV, 0, 31.75)).ToArray());
            return new Fit { Base = baseV, X = Math.Round(x * 2) / 2, Steps = steps, Avg = total };
        }

        static double At(GHub.GunVals v, int k) =>
            v.Base + (v.Steps == null || v.Steps.Length == 0 ? 0 : v.Steps[Math.Min(k, v.Steps.Length - 1)]);

        // ออโต้จูน: ขยับจากค่าเดิมไปทางผลที่วัดได้ทีละ gain (ไม่กระโดดทีเดียว กันแกว่งจากชุดที่มือพลาด)
        static Fit Blend(GHub.GunVals cur, Fit f, double gain)
        {
            int len = f.Steps.Length;
            var total = new double[len];
            for (int k = 0; k < len; k++)
            {
                double c = At(cur, k), t = f.Base + f.Steps[k];
                total[k] = c + gain * (t - c);
            }
            var b = FromTotal(total, cur.X + gain * (f.X - cur.X));
            // ขยับน้อยจนปัดแล้วไม่เปลี่ยน → ใช้ผลที่วัดได้ตรงๆ จะได้ไม่ค้าง
            bool same = b.Base == cur.Base && b.X == cur.X && cur.Steps != null && LiveLink.SameGraph(b.Steps, cur.Steps);
            return same ? f : b;
        }

        // ---------------------------------------------------------------- ออโต้จูน
        //   เปิดมาโครแล้วยิงไปเรื่อยๆ ทุก AUTO_N ชุด → ปรับค่า + ส่งเข้า G Hub เอง → วัดรอบใหม่
        //   นิ่ง (มือแทบไม่ต้องแก้) ติดกัน 2 รอบ = เสร็จ หยุดเอง
        const int AUTO_N = 3, AUTO_MAX_ROUNDS = 12;
        const double AUTO_GAIN = 0.7;
        bool auto;
        int goodRounds, autoRounds;
        GHub.GunVals lastUsed;

        void ToggleAuto()
        {
            auto = !auto;
            goodRounds = autoRounds = 0;
            sprays.RemoveAll(s => !s.Tuned);
            Log.Info("calib auto " + (auto ? "on" : "off"));
            pop(auto ? "ออโต้จูน: เปิด — เปิดมาโครแล้วยิงได้เลย" : "ออโต้จูน: ปิด");
            Refresh();
        }

        void AutoStep()
        {
            var tuned = sprays.Where(s => s.Tuned).ToList();
            if (tuned.Count < AUTO_N || lastUsed == null) return;
            autoRounds++;
            var (_, short_, good) = Verdict(tuned.Average(s => s.Corr), tuned.Average(s => s.CorrRms));
            if (Math.Abs(tuned.Average(s => s.CorrX)) >= 0.3) good = false;
            sprays.Clear();
            round++;
            if (good)
            {
                goodRounds++;
                if (goodRounds >= 2)
                {
                    auto = false;
                    Log.Info($"calib auto done {names[gun]}: base {GHub.Fmt(lastUsed.Base)} x {GHub.Fmt(lastUsed.X)}");
                    pop($"✓  ออโต้จูนเสร็จ — {names[gun]} นิ่งแล้ว");
                }
                else pop($"ออโต้จูน: นิ่งแล้ว ✓ ยิงยืนยันอีก {AUTO_N} ชุด");
                Refresh();
                return;
            }
            goodRounds = 0;
            var f = Analyze(tuned);
            if (f == null) { Refresh(); return; }
            var b = Blend(lastUsed, f, AUTO_GAIN);
            apply(gun, b);
            Log.Info($"calib auto round {autoRounds} {names[gun]}: {short_} base {GHub.Fmt(lastUsed.Base)}→{GHub.Fmt(b.Base)} x {GHub.Fmt(lastUsed.X)}→{GHub.Fmt(b.X)}");
            pop($"ออโต้จูนรอบ {autoRounds}: {short_} · base {GHub.Fmt(lastUsed.Base)}→{GHub.Fmt(b.Base)}");
            if (autoRounds >= AUTO_MAX_ROUNDS)
            {
                auto = false;
                pop("ออโต้จูน: ครบ 12 รอบแล้ว หยุดก่อน (มือแต่ละชุดต่างกันมาก ลองยิงนิ่งๆ แล้วเริ่มใหม่)");
            }
            Refresh();
        }

        void Apply()
        {
            var f = Analyze(sprays);
            if (f == null) return;
            apply(gun, f);
            Log.Info($"calib apply {names[gun]} round {round}: base {GHub.Fmt(f.Base)} x {GHub.Fmt(f.X)} from {f.Used} sprays");
            // รอบถัดไปวัดจากค่าใหม่ (ชุดเก่าวัดตอนมาโครยังใช้ค่าเดิม)
            sprays.Clear();
            round++;
            Refresh();
            status.Text = $"ใช้ค่ากับ {names[gun]} แล้ว (รอบ {round - 1}) — เปิดมาโครแล้วยิงจูนละเอียดต่อ 3 ชุด จนขึ้นว่า \"นิ่งแล้ว\"";
            status.Foreground = new SolidColorBrush(Ice);
        }

        // ---------------------------------------------------------------- หน้าจอ

        void Refresh()
        {
            for (int i = 0; i < names.Length; i++)
            {
                tabs[i].Content = $"{i + 1} · {names[i]}";
                if (i == gun) tabs[i].BorderBrush = new SolidColorBrush(Ice);
                else tabs[i].ClearValue(Control.BorderBrushProperty);
            }
            var f = Analyze(sprays);
            int n = sprays.Count;
            status.Foreground = Text;
            status.Text = n == 0 ? $"พร้อมอัดให้ {names[gun]} — ไปยิงในเกมได้เลย (หน้าต่างนี้เปิดทิ้งไว้ได้)"
                        : $"อัดแล้ว {n} ชุด" + (n < 3 ? " — แนะนำ 3–5 ชุด" : " — พอใช้ได้แล้ว อัดเพิ่มได้อีก");
            undoBtn.IsEnabled = n > 0;
            applyBtn.IsEnabled = f != null && n >= 2;
            applyBtn.Content = $"ใช้ค่านี้กับ {names[gun]}";
            result.Foreground = Muted;
            result.Text = f == null
                ? "ผลลัพธ์จะขึ้นตรงนี้ (ต้องยิงค้างอย่างน้อย 0.5 วินาที)"
                : $"ผล: base {GHub.Fmt(f.Base)} · x {GHub.Fmt(f.X)} · กราฟยาว {GHub.Fmt(f.Steps.Length / 100.0)} วิ" +
                  $"  (ใช้ {f.Used} ชุด" + (f.Dropped > 0 ? $", ตัดทิ้ง {f.Dropped}" : "") + ")";

            // จูนละเอียด: บอกว่าเป้านิ่งหรือยัง
            var tuned = sprays.Where(s => s.Tuned).ToList();
            if (tuned.Count > 0)
            {
                var (text, _, good) = Verdict(tuned.Average(s => s.Corr), tuned.Average(s => s.CorrRms));
                double cx = tuned.Average(s => s.CorrX);
                if (Math.Abs(cx) >= 0.3) { text += $" · ซ้าย/ขวา: มือแก้ไป{(cx > 0 ? "ขวา" : "ซ้าย")} {Math.Abs(cx):0.0}"; good = false; }
                var ratios = tuned.Select(s => s.Ratio).Where(r => !double.IsNaN(r)).ToList();
                if (ratios.Count > 0 && Math.Abs(ratios.Average() - 1) > 0.25)
                    text += $"\nหมายเหตุ: มาโครขยับจริง ×{ratios.Average():0.00} ของค่าที่ตั้ง — ระบบแปลงหน่วยให้แล้ว";
                status.Text = $"รอบ {round} · จูนแล้ว {tuned.Count} ชุด — {text}";
                status.Foreground = new SolidColorBrush(good ? Color.FromRgb(0x22, 0xC5, 0x5E) : Gold);
                if (good) applyBtn.IsEnabled = false;          // นิ่งแล้ว ไม่ต้องเปลี่ยนค่า
            }

            autoBtn.Content = auto ? "■  หยุดออโต้จูน" : "▶  ออโต้จูน";
            if (auto)
            {
                applyBtn.IsEnabled = false;                     // ออโต้จูนใช้ค่าเอง
                status.Text = $"ออโต้จูน {names[gun]} · รอบ {autoRounds + 1} · ยิงแล้ว {tuned.Count}/{AUTO_N}" +
                              (goodRounds > 0 ? " · นิ่งแล้ว รอยืนยัน" : "") +
                              " — เปิดมาโคร ยิงค้าง แล้วดึงแก้เฉพาะส่วนที่เป้ายังขยับ";
                status.Foreground = new SolidColorBrush(Gold);
            }
            Draw(f);
        }

        void Draw(Fit f)
        {
            plot.Children.Clear();
            var series = sprays.Select(s => Bars(s.Y)).ToList();
            double[] fitBars = f == null ? null : Bars(f.Steps.Select(v => v + f.Base).ToArray());
            int nb = Math.Max(30, series.Select(b => b.Length).DefaultIfEmpty(0).Max());
            double ymax = Math.Max(6, Math.Ceiling(series.SelectMany(b => b).DefaultIfEmpty(0).Max() + 1));
            double bw = (PW - PADL - PADR) / nb;
            double Y(double v) => PH - PADB - Math.Clamp(v, 0, ymax) / ymax * (PH - PADB - PADT);

            double step = ymax <= 8 ? 1 : ymax <= 20 ? 2 : 5;
            for (double v = 0; v <= ymax + 1e-9; v += step)
            {
                plot.Children.Add(new Line { X1 = PADL, X2 = PW - PADR, Y1 = Y(v), Y2 = Y(v), Stroke = GridBrush });
                Label(GHub.Fmt(v), 0, Y(v) - 8, 32, TextAlignment.Right);
            }
            for (int b = 0; b <= nb; b += 5)
                Label(GHub.Fmt(b / 10.0) + " วิ", PADL + b * bw - 20, PH - PADB + 4, 40, TextAlignment.Center);

            // แต่ละชุด = เส้นจาง, ผล = แท่ง
            if (fitBars != null)
                for (int b = 0; b < fitBars.Length; b++)
                {
                    var r = new Rectangle
                    {
                        Width = Math.Max(2, bw - 3), Height = Math.Max(1, PH - PADB - Y(fitBars[b])), RadiusX = 3, RadiusY = 3,
                        Fill = new LinearGradientBrush(Ice, Blue, 90), Opacity = 0.55
                    };
                    Canvas.SetLeft(r, PADL + b * bw + 1.5);
                    Canvas.SetTop(r, Y(fitBars[b]));
                    plot.Children.Add(r);
                }
            foreach (var bars in series)
            {
                var pl = new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0xC9, 0xA5, 0x4A)), StrokeThickness = 1.5 };
                for (int b = 0; b < bars.Length; b++) pl.Points.Add(new Point(PADL + (b + 0.5) * bw, Y(bars[b])));
                plot.Children.Add(pl);
            }
            if (f != null)
            {
                double y = Y(f.Base);
                plot.Children.Add(new Line { X1 = PADL, X2 = PW - PADR, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Ice), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } });
                Label("base", PW - PADR - 34, y - 15, 34, TextAlignment.Right);
            }
        }

        static double[] Bars(double[] steps)
        {
            int n = (steps.Length + 9) / 10;
            return Enumerable.Range(0, n).Select(b => steps.Skip(b * 10).Take(10).Average()).ToArray();
        }

        void Label(string text, double x, double y, double w, TextAlignment align)
        {
            var t = new TextBlock { Text = text, FontSize = 10.5, Foreground = Muted, Width = w, TextAlignment = align };
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            plot.Children.Add(t);
        }
    }
}
