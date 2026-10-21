using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

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
            public double[] CorrBars;       // มือดึงแก้ต่อแท่ง 100 ms
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
        readonly Func<int, GHub.GunVals> wanted;        // ค่าใน GunBeep ตอนนี้ (อาจยังส่งเข้า G Hub ไม่เสร็จ)
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

        // เวลามาตรฐานของแต่ละชุด: วัดแค่ช่วงนี้ทุกชุดเท่ากัน (ครบแล้วป้ายบอกให้ปล่อย) — หลังจากนั้นมาโครดึงคงที่
        static readonly double[] Windows = { 2.5, 3.0, 3.5, 4.0 };
        double windowSec = 3.0;
        readonly DispatcherTimer cutTimer = new DispatcherTimer();
        Button winBtn;

        public Calibrator(string[] gunNames, int startGun, Func<int> activeGun, Func<int, GHub.GunVals> macroValues,
                          Func<int, GHub.GunVals> currentValues, Action<int, Fit> onApply, Action<string> onPop)
        {
            names = gunNames;
            gun = Math.Clamp(startGun - 1, 0, names.Length - 1);
            macroGun = activeGun;
            inMacro = macroValues;
            wanted = currentValues;
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
            winBtn = Btn("", () =>
            {
                windowSec = Windows[(Array.IndexOf(Windows, windowSec) + 1) % Windows.Length];
                sprays.Clear();                                 // ชุดเก่าวัดคนละความยาว
                Refresh();
            }, 6);
            winBtn.ToolTip = "ความยาวที่วัดต่อชุด (≈ 1 แม็ก) — กดเพื่อเปลี่ยน";
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(undoBtn);
            left.Children.Add(clearBtn);
            left.Children.Add(autoBtn);
            left.Children.Add(winBtn);
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
            stack.Children.Add(BuildHolesPanel());
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
            cutTimer.Tick += (s, e) =>
            {
                cutTimer.Stop();
                if (down && !ignoreThis) pop($"✓  ครบ {GHub.Fmt(windowSec)} วิ — ปล่อยได้");
            };
            plot.Background = Brushes.Transparent;              // รับเมาส์ทั้งพื้นที่กราฟ
            plot.Cursor = Cursors.IBeam;
            plot.MouseLeftButtonDown += PlotDown;
            plot.MouseMove += PlotMove;
            plot.MouseLeftButtonUp += PlotUp;

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

        // ---------------------------------------------------------------- จูนจากรอยกระสุน
        //   เปิดมาโคร มือนิ่ง ยิงหมดแม็กใส่กำแพง แล้วดูรอย: ช่วงไหนหลุดขึ้น = ช่วงนั้นดึงเพิ่ม, หลุดลง = ดึงน้อยลง
        //   ไม่ต้องดึงแก้เอง (มือที่ดึงขึ้นลงตามแรงดีดทำให้ค่าแกว่ง) และไม่อ่านภาพจากเกม — ใช้ตาคนดู
        //   เลือกช่วงเวลาด้วยการลากบนกราฟ (นับจากเริ่มกดยิง) แล้วกด หลุดขึ้น/หลุดลง เฉพาะช่วงนั้น
        const double HOLE_STEP = 0.5, SHOT_SEC = 0.086;      // ระยะห่างนัด ~M416 (ไว้บอกเลขนัดคร่าวๆ)
        readonly Stack<(int gun, GHub.GunVals v)> holeUndo = new();
        Button holeUndoBtn;
        readonly TextBlock selText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Width = 210 };
        double selA = 0, selB = 3;                            // ช่วงที่เลือก (วินาที)
        bool selDrag;
        double dragFrom, lastBw = 1;
        int lastNb = 30;

        UIElement BuildHolesPanel()
        {
            var box = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            box.Children.Add(new TextBlock
            {
                Text = "จูนจากรอยกระสุน (ไม่ต้องดึงแก้)", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Text
            });
            box.Children.Add(new TextBlock
            {
                FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap, MaxWidth = PW + 16,
                Text = "เปิดมาโคร (ปืนเดียวกับแท็บนี้) เล็งจุดเดียว มือนิ่ง ยิงหมดแม็ก แล้วดูรอยบนกำแพง → " +
                       "ลากบนกราฟเพื่อเลือกช่วงเวลาที่หลุด (นับจากเริ่มกดยิง, เส้นขาว = แรงดึงของมาโครตอนนี้) → กด ▲/▼ ทีละ 0.5 → ยิงดูใหม่"
            });

            var row1 = new StackPanel { Orientation = Orientation.Horizontal };
            selText.Foreground = new SolidColorBrush(Gold);
            row1.Children.Add(selText);
            row1.Children.Add(Btn("ทั้งแม็ก", () => Select(0, 99), 0));
            row1.Children.Add(Btn("0–0.8 วิ", () => Select(0, 0.8), 4));
            row1.Children.Add(Btn("0.8–2 วิ", () => Select(0.8, 2), 4));
            row1.Children.Add(Btn("2 วิ ขึ้นไป", () => Select(2, 99), 4));
            box.Children.Add(row1);

            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var up = Btn("▲ ช่วงนี้หลุดขึ้น", () => Nudge(SelName(), selA, selB, +HOLE_STEP, 0), 0);
            var dn = Btn("▼ ช่วงนี้หลุดลง", () => Nudge(SelName(), selA, selB, -HOLE_STEP, 0), 4);
            up.BorderBrush = dn.BorderBrush = new SolidColorBrush(Gold);
            row2.Children.Add(up);
            row2.Children.Add(dn);
            row2.Children.Add(Btn("◀ หลุดซ้าย", () => Nudge("ซ้าย/ขวา", 0, 0, 0, +HOLE_STEP), 16));
            row2.Children.Add(Btn("หลุดขวา ▶", () => Nudge("ซ้าย/ขวา", 0, 0, 0, -HOLE_STEP), 4));
            row2.Children.Add(Btn("〰 เกลี่ยกราฟ", () => SmoothCurrent(false), 16));
            holeUndoBtn = Btn("↶ ย้อน", HoleUndo, 6);
            holeUndoBtn.IsEnabled = false;
            row2.Children.Add(holeUndoBtn);
            box.Children.Add(row2);
            UpdateSelText();
            return box;
        }

        double End => lastNb / 10.0;

        void Select(double a, double b)
        {
            selA = Math.Max(0, Math.Min(a, End - 0.1));
            selB = Math.Max(selA + 0.1, Math.Min(b, End));
            UpdateSelText();
            Draw(Analyze(sprays));
        }

        string SelName() => $"{GHub.Fmt(selA)}–{(selB >= End - 1e-9 ? "จบ" : GHub.Fmt(selB))} วิ";

        void UpdateSelText()
        {
            int s1 = (int)(selA / SHOT_SEC) + 1, s2 = (int)Math.Ceiling(selB / SHOT_SEC);
            selText.Text = $"ช่วง {SelName()}" + (selB < End - 1e-9 ? $"  (≈ นัด {s1}–{s2})" : $"  (นัด {s1} ขึ้นไป)");
        }

        // ลากบนกราฟ = เลือกช่วงเวลา (ปัดทีละ 0.1 วิ = 1 แท่ง)
        double SecAt(double x) => Math.Clamp(Math.Round((x - PADL) / lastBw) / 10.0, 0, End);

        void PlotDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;                                   // ไม่ให้ลากทั้งหน้าต่าง
            selDrag = true;
            plot.CaptureMouse();
            dragFrom = SecAt(e.GetPosition(plot).X);
            Select(dragFrom, dragFrom + 0.1);
        }

        void PlotMove(object sender, MouseEventArgs e)
        {
            if (!selDrag) return;
            double t = SecAt(e.GetPosition(plot).X);
            Select(Math.Min(dragFrom, t), Math.Max(dragFrom + 0.1, t));
        }

        void PlotUp(object sender, MouseButtonEventArgs e)
        {
            if (!selDrag) return;
            selDrag = false;
            plot.ReleaseMouseCapture();
        }

        // หลุดขึ้น = ช่วงนั้นดึงลงเพิ่ม (+), หลุดลง = ดึงน้อยลง (−), หลุดขวา = x ไปทางซ้าย (−)
        void Nudge(string part, double fromSec, double toSec, double dy, double dx)
        {
            var cur = wanted(gun);
            if (cur == null) { pop("ยังไม่พบค่าปืนนี้"); return; }
            if (macroGun() != 0 && macroGun() - 1 != gun) pop($"หมายเหตุ: มาโครใช้ปืน {macroGun()} อยู่ — ค่านี้ปรับให้ {names[gun]}");
            holeUndo.Push((gun, Copy(cur)));
            int len = Math.Max(cur.Steps?.Length ?? 0, 400);          // ช่วงท้ายยาวพอเสมอ (หลังกราฟจบใช้ค่าสุดท้าย)
            var total = new double[len];
            int a = (int)Math.Round(fromSec * 100), z = toSec >= End - 1e-9 ? len : (int)Math.Round(toSec * 100);
            for (int k = 0; k < len; k++) total[k] = Math.Max(0, At(cur, k) + (k >= a && k < z ? dy : 0));
            var f = FromTotal(total, cur.X + dx, cur.Base);
            apply(gun, f);
            holeUndoBtn.IsEnabled = true;
            string what = dx != 0 ? (dx > 0 ? "ดึงไปทางขวาเพิ่ม" : "ดึงไปทางซ้ายเพิ่ม") : (dy > 0 ? "ดึงลงเพิ่ม" : "ดึงลงน้อยลง");
            Log.Info($"calib holes {names[gun]} {part} dy {dy} dx {dx} → base {GHub.Fmt(f.Base)} x {GHub.Fmt(f.X)}");
            pop($"{names[gun]} · {part}: {what} 0.5 — ยิงดูใหม่ได้เลย");
            Draw(Analyze(sprays));
            status.Text = $"{names[gun]} · {part}: {what} 0.5 (base {GHub.Fmt(f.Base)} · ซ้าย/ขวา {GHub.Fmt(f.X)}) — รอป้าย \"ส่งค่าเข้า G Hub แล้ว\" แล้วยิงดูใหม่";
            status.Foreground = new SolidColorBrush(Ice);
        }

        void HoleUndo()
        {
            if (holeUndo.Count == 0) return;
            var (g, v) = holeUndo.Pop();
            apply(g, new Fit { Base = v.Base, X = v.X, Steps = v.Steps ?? new double[] { 0 }, Avg = v.Steps ?? new double[] { 0 } });
            holeUndoBtn.IsEnabled = holeUndo.Count > 0;
            pop($"ย้อนค่า {names[g]} แล้ว");
            Log.Info($"calib holes undo {names[g]} → base {GHub.Fmt(v.Base)} x {GHub.Fmt(v.X)}");
        }

        static GHub.GunVals Copy(GHub.GunVals v) =>
            new GHub.GunVals { Base = v.Base, X = v.X, Steps = v.Steps?.ToArray() };

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
                cutTimer.Interval = TimeSpan.FromSeconds(windowSec);
                cutTimer.Stop();
                cutTimer.Start();
            }
            if (down && (dx != 0 || dy != 0)) cur.Add((now - t0, dx, dy, dev == hand));
            if (left == -1 && down && dev == hand)
            {
                down = false;
                cutTimer.Stop();
                double ms = now - t0;
                if (ignoreThis) return;
                if (ms < MIN_MS) return;                        // แตะยิง / คลิกทั่วไป
                // ทุกชุดวัดยาวเท่ากัน: เกินเวลามาตรฐานตัดทิ้ง, ออโต้จูนต้องยิงให้ครบ
                double cutMs = windowSec * 1000;
                if (ms >= cutMs - 150) { cur = cur.Where(e => e.t < cutMs).ToList(); ms = cutMs; }
                else if (auto) { pop($"ยิงไม่ครบ {GHub.Fmt(windowSec)} วิ (ชุดนี้ไม่นับ) — ยิงค้างจนป้ายบอกให้ปล่อย"); return; }
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
                    if (sprays.Count < AUTO_N) pop($"ออโต้จูน {sprays.Count}/{AUTO_N} · มือแก้เฉลี่ย {s.Corr:+0.0;-0.0;0}");
                    Refresh();
                    AutoStep();
                    return;
                }
                pop(s.Tuned ? $"จูนชุดที่ {sprays.Count} · มือแก้เฉลี่ย {s.Corr:+0.0;-0.0;0}{fit}"
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
                CorrBars = bars,
                Ratio = ratio,
            };
        }

        // แยก "ค่าที่ผิดจริง" ออกจาก "มือสั่น": ส่วนที่มือแก้เหมือนกันทุกชุด = มาโครดึงผิดจริง
        //   ส่วนที่แต่ละชุดไม่เหมือนกัน = มือสั่นตามธรรมชาติ ห้ามเอามาปรับ (ไม่งั้นค่าจะเดินไปเรื่อยๆ ไม่จบ)
        //   ความสั่นของมือวัดรวมจากทุกแท่ง (แม่นกว่าดูทีละแท่ง) แล้วเฉลี่ยกับแท่งข้างเคียง (แรงดีดเปลี่ยนแบบค่อยเป็นค่อยไป)
        //   ช่วงไหนค่าเฉลี่ยเกิน 3 เท่าของความคลาดเคลื่อน และ ≥ 0.25 ถึงนับว่าผิดจริง
        public class Sys { public double[] Fix; public double FixX, Mean, Rms; public bool Good; }

        static Sys Systematic(List<Spray> tuned)
        {
            int nb = tuned.Max(s => s.CorrBars.Length);
            var mean = new double[nb];
            var cnt = new int[nb];
            double ss = 0;
            int df = 0;
            for (int b = 0; b < nb; b++)
            {
                var v = tuned.Where(s => s.CorrBars.Length > b).Select(s => s.CorrBars[b]).ToList();
                cnt[b] = v.Count;
                if (v.Count == 0) continue;
                mean[b] = v.Average();
                ss += v.Sum(a => (a - mean[b]) * (a - mean[b]));
                df += v.Count - 1;
            }
            double sd = df > 0 ? Math.Sqrt(ss / df) : double.MaxValue;
            var fix = new double[nb];
            for (int b = 0; b < nb; b++)
            {
                double sum = 0;
                int n = 0, w = 0;
                for (int j = Math.Max(0, b - 1); j <= Math.Min(nb - 1, b + 1); j++)
                    if (cnt[j] >= 2) { sum += mean[j] * cnt[j]; n += cnt[j]; w++; }
                if (n == 0) continue;
                double m = sum / n, se = sd / Math.Sqrt(n);
                if (Math.Abs(m) >= 0.25 && Math.Abs(m) > 3 * se) fix[b] = m;
            }
            fix = SmoothBars(fix);                              // แก้เป็นช่วงต่อเนื่อง ไม่ใช่ทีละแท่งหยักๆ
            double rms = Math.Sqrt(fix.Average(f => f * f));
            var xs = tuned.Select(s => s.CorrX).ToList();
            double mx = xs.Average(), sx = xs.Count > 1 ? Math.Sqrt(xs.Sum(a => (a - mx) * (a - mx)) / (xs.Count - 1)) : double.MaxValue;
            double fixX = Math.Abs(mx) >= 0.25 && Math.Abs(mx) > 4.3 * sx / Math.Sqrt(xs.Count) ? mx : 0;
            return new Sys
            {
                Fix = fix, FixX = fixX, Mean = tuned.Average(s => s.Corr), Rms = rms,
                Good = rms < 0.45 && Math.Abs(fixX) < 0.5,     // เศษเล็กกว่าขั้น base (0.5) ปรับต่อก็ไม่ดีขึ้น
            };
        }

        static string SysText(Sys s)
        {
            if (s.Good) return "นิ่งแล้ว ✓ ที่มือแก้เหลือแค่มือสั่นปกติ";
            var parts = new List<string>();
            if (s.Rms >= 0.45)
            {
                double up = s.Fix.Where(f => f > 0).Sum(), down = -s.Fix.Where(f => f < 0).Sum();
                parts.Add(up > down * 2 ? "มาโครดึงไม่พอบางช่วง" : down > up * 2 ? "มาโครดึงเกินบางช่วง" : "กราฟยังไม่ตรงบางช่วง");
            }
            if (Math.Abs(s.FixX) >= 0.5) parts.Add($"มือแก้ไป{(s.FixX > 0 ? "ขวา" : "ซ้าย")}ทุกชุด");
            return string.Join(" · ", parts);
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

            var f = FromTotal(avg, cntX > 0 ? sumX / cntX : 0, smooth: true);
            f.Used = use.Count;
            f.Dropped = dropped;
            return f;
        }

        // แรงดึงรวมต่อ 10 ms → base (ส่วนต่ำสุด) + กราฟ (ส่วนที่เกิน) + x
        //   keepBase = ใช้ base เดิมถ้ายังไม่เกินช่วงต่ำสุด (กัน base เดินตามเสียงรบกวนทีละนิด)
        static Fit FromTotal(double[] total, double x, double? keepBase = null, bool smooth = false)
        {
            if (smooth) total = SmoothSteps(total);
            var bars = LiveLink.Quantize(total);
            double min = bars.Min(), max = bars.Max();
            double baseV = keepBase.HasValue && keepBase.Value <= min + 1e-9 ? keepBase.Value : Math.Max(0, Math.Floor(min * 2) / 2);
            if (max - baseV > 31.75) baseV = Math.Ceiling((max - 31.75) * 2) / 2;   // กราฟส่งได้สูงสุด 31.75
            var steps = LiveLink.Fit(bars.Select(v => Math.Clamp(v - baseV, 0, 31.75)).ToArray());
            return new Fit { Base = baseV, X = Math.Round(x * 2) / 2, Steps = steps, Avg = total };
        }

        // เกลี่ยกราฟ: แรงดีดจริงเปลี่ยนแบบค่อยเป็นค่อยไป หยักขึ้นลงทุกแท่ง = มือสั่นที่ติดมา → เป้าลงยาว/ขึ้นยาว
        //   ถัวน้ำหนัก 1-2-3-2-1 ต่อแท่ง 100 ms (แท่งแรกคงไว้ แรงดีดนัดแรกมักต่างจากที่เหลือ)
        static double[] SmoothBars(double[] b)
        {
            int[] w = { 1, 2, 3, 2, 1 };
            var r = new double[b.Length];
            for (int i = 0; i < b.Length; i++)
            {
                double sum = 0, ws = 0;
                for (int j = -2; j <= 2; j++)
                {
                    int k = i + j;
                    if (k < 0 || k >= b.Length) continue;
                    sum += b[k] * w[j + 2];
                    ws += w[j + 2];
                }
                r[i] = sum / ws;
            }
            if (b.Length > 0) r[0] = b[0];
            return r;
        }

        static double[] SmoothSteps(double[] steps)
        {
            var bars = SmoothBars(Bars(steps));
            return Enumerable.Range(0, steps.Length).Select(k => bars[k / 10]).ToArray();
        }

        // ความหยักของกราฟ = ค่าเฉลี่ยที่แท่งติดกันต่างกัน
        static double Roughness(double[] bars) =>
            bars.Length < 2 ? 0 : Enumerable.Range(1, bars.Length - 1).Average(i => Math.Abs(bars[i] - bars[i - 1]));

        static double At(GHub.GunVals v, int k) =>
            v.Base + (v.Steps == null || v.Steps.Length == 0 ? 0 : v.Steps[Math.Min(k, v.Steps.Length - 1)]);

        // ออโต้จูน: แก้ค่าเดิมเฉพาะช่วงที่ผิดจริง ทีละ gain (ไม่กระโดดทีเดียว)
        static Fit Adjust(GHub.GunVals cur, Sys s, double gain)
        {
            int len = s.Fix.Length * 10;                         // กราฟจบที่เวลามาตรฐาน หลังจากนั้นสคริปต์ดึงค่าสุดท้ายคงที่
            var total = new double[len];
            for (int k = 0; k < len; k++)
                total[k] = At(cur, k) + gain * s.Fix[k / 10];
            return FromTotal(total, cur.X + gain * s.FixX, cur.Base);
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
            sprays.Clear();
            if (auto) SmoothCurrent(true);
            Log.Info("calib auto " + (auto ? "on" : "off"));
            pop(auto ? "ออโต้จูน: เปิด — เปิดมาโครแล้วยิงได้เลย" : "ออโต้จูน: ปิด");
            Refresh();
        }

        // เกลี่ยกราฟของปืนนี้ให้เรียบ (ภายในเวลามาตรฐาน หลังจากนั้นดึงคงที่) — onlyIfRough = ทำเฉพาะตอนหยักจริง
        void SmoothCurrent(bool onlyIfRough)
        {
            var cur = wanted(gun);
            if (cur == null) return;
            int len = (int)Math.Round(windowSec * 100);
            var total = Enumerable.Range(0, len).Select(k => At(cur, k)).ToArray();
            double rough = Roughness(Bars(total));
            if (onlyIfRough && rough < 0.6) return;
            holeUndo.Push((gun, Copy(cur)));
            if (holeUndoBtn != null) holeUndoBtn.IsEnabled = true;
            var f = FromTotal(total, cur.X, cur.Base, smooth: true);
            apply(gun, f);
            Log.Info($"calib smooth {names[gun]} rough {rough:0.00} → {Roughness(Bars(f.Steps)):0.00}");
            pop($"เกลี่ยกราฟ {names[gun]} ให้เรียบแล้ว (เดิมหยัก {rough:0.0})");
            Draw(Analyze(sprays));
        }

        void AutoStep()
        {
            var tuned = sprays.Where(s => s.Tuned).ToList();
            if (tuned.Count < AUTO_N || lastUsed == null) return;
            autoRounds++;
            var sys = Systematic(tuned);
            string short_ = SysText(sys);
            Log.Info($"calib auto check {names[gun]}: mean {sys.Mean:0.00} sys {sys.Rms:0.00} x {sys.FixX:0.00} good {sys.Good}");
            sprays.Clear();
            round++;
            if (sys.Good)
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
            var b = Adjust(lastUsed, sys, AUTO_GAIN);
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
                string text;
                bool good = false;
                if (tuned.Count < 2) text = "ยิงอีกอย่างน้อย 1 ชุด ถึงจะแยกได้ว่าผิดจริงหรือแค่มือสั่น";
                else
                {
                    var sys = Systematic(tuned);
                    good = sys.Good;
                    text = SysText(sys);
                }
                var ratios = tuned.Select(s => s.Ratio).Where(r => !double.IsNaN(r)).ToList();
                if (ratios.Count > 0 && Math.Abs(ratios.Average() - 1) > 0.25)
                    text += $"\nหมายเหตุ: มาโครขยับจริง ×{ratios.Average():0.00} ของค่าที่ตั้ง — ระบบแปลงหน่วยให้แล้ว";
                status.Text = $"รอบ {round} · จูนแล้ว {tuned.Count} ชุด — {text}";
                status.Foreground = new SolidColorBrush(good ? Color.FromRgb(0x22, 0xC5, 0x5E) : Gold);
                if (good) applyBtn.IsEnabled = false;          // นิ่งแล้ว ไม่ต้องเปลี่ยนค่า
            }

            autoBtn.Content = auto ? "■  หยุดออโต้จูน" : "▶  ออโต้จูน";
            winBtn.Content = $"⏱ วัด {GHub.Fmt(windowSec)} วิ";
            if (auto)
            {
                applyBtn.IsEnabled = false;                     // ออโต้จูนใช้ค่าเอง
                status.Text = $"ออโต้จูน {names[gun]} · รอบ {autoRounds + 1} · ยิงแล้ว {tuned.Count}/{AUTO_N}" +
                              (goodRounds > 0 ? " · นิ่งแล้ว รอยืนยัน" : "") +
                              $" — เปิดมาโคร ยิงค้างจนป้ายบอก \"ครบ {GHub.Fmt(windowSec)} วิ\" ดึงแก้เฉพาะส่วนที่เป้ายังขยับ";
                status.Foreground = new SolidColorBrush(Gold);
            }
            Draw(f);
        }

        void Draw(Fit f)
        {
            plot.Children.Clear();
            var series = sprays.Select(s => Bars(s.Y)).ToList();
            double[] fitBars = f == null ? null : Bars(f.Steps.Select(v => v + f.Base).ToArray());
            var mv = wanted?.Invoke(gun);
            double[] macroBars = mv == null ? null
                : Bars(Enumerable.Range(0, Math.Max(mv.Steps?.Length ?? 0, 300)).Select(k => At(mv, k)).ToArray());
            int nb = Math.Max(30, Math.Max(series.Select(b => b.Length).DefaultIfEmpty(0).Max(), macroBars?.Length ?? 0));
            double ymax = Math.Max(6, Math.Ceiling(Math.Max(series.SelectMany(b => b).DefaultIfEmpty(0).Max(),
                                                            macroBars == null ? 0 : macroBars.Max()) + 1));
            double bw = (PW - PADL - PADR) / nb;
            lastNb = nb;
            lastBw = bw;
            double Y(double v) => PH - PADB - Math.Clamp(v, 0, ymax) / ymax * (PH - PADB - PADT);

            double step = ymax <= 8 ? 1 : ymax <= 20 ? 2 : 5;
            for (double v = 0; v <= ymax + 1e-9; v += step)
            {
                plot.Children.Add(new Line { X1 = PADL, X2 = PW - PADR, Y1 = Y(v), Y2 = Y(v), Stroke = GridBrush });
                Label(GHub.Fmt(v), 0, Y(v) - 8, 32, TextAlignment.Right);
            }
            for (int b = 0; b <= nb; b += 5)
                Label(GHub.Fmt(b / 10.0) + " วิ", PADL + b * bw - 20, PH - PADB + 4, 40, TextAlignment.Center);

            // ช่วงเวลาที่เลือกไว้จูนจากรอยกระสุน
            double sa = Math.Min(selA, nb / 10.0), sb = Math.Min(selB, nb / 10.0);
            if (sb > sa)
            {
                var sel = new Rectangle
                {
                    Width = Math.Max(2, (sb - sa) * 10 * bw), Height = PH - PADB - PADT,
                    Fill = new SolidColorBrush(Color.FromArgb(0x2A, 0xC9, 0xA5, 0x4A)),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0xC9, 0xA5, 0x4A)), StrokeDashArray = new DoubleCollection { 3, 3 },
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(sel, PADL + sa * 10 * bw);
                Canvas.SetTop(sel, PADT);
                plot.Children.Add(sel);
            }

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
            // แรงดึงของมาโครตอนนี้ (เส้นขาวแบบขั้นบันได)
            if (macroBars != null)
            {
                var ml = new Polyline { Stroke = Brushes.White, StrokeThickness = 2, Opacity = 0.85, IsHitTestVisible = false };
                for (int b = 0; b < macroBars.Length; b++)
                {
                    ml.Points.Add(new Point(PADL + b * bw, Y(macroBars[b])));
                    ml.Points.Add(new Point(PADL + (b + 1) * bw, Y(macroBars[b])));
                }
                plot.Children.Add(ml);
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
