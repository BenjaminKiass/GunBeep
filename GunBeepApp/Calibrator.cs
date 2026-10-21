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
    // อัดค่าแรงดึงอัตโนมัติ: ปิดมาโคร → ยิงค้างใส่กำแพงแล้วลากเมาส์ลงกดเป้าให้นิ่งเอง 3-5 ชุด
    //   GunBeep อัดการเคลื่อนเมาส์จริง (Raw Input) ทุก 10 ms ตั้งแต่กดยิงจนปล่อย
    //   เฉลี่ยทุกชุด → base (แรงดึงต่ำสุด) + กราฟ (ส่วนที่เพิ่มตามเวลา) + x (ดึงซ้าย/ขวาเฉลี่ย)
    public class Calibrator : Window
    {
        public class Spray { public double[] Y, X; public double Ms; }

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
        readonly Func<bool> macroOn;
        readonly Action<int, Fit> apply;
        readonly Action<string> pop;
        readonly Style keyBtn;
        readonly Button[] tabs = new Button[3];
        readonly Button applyBtn, undoBtn;
        readonly TextBlock status = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4), TextWrapping = TextWrapping.Wrap };
        readonly TextBlock result = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        readonly Canvas plot = new Canvas { Width = PW, Height = PH, ClipToBounds = true };
        readonly List<Spray> sprays = new();
        readonly Stopwatch clock = Stopwatch.StartNew();

        RawMouse raw;
        int gun;
        bool down, ignoreThis;
        double t0;
        List<(double t, int dx, int dy)> cur;

        public Calibrator(string[] gunNames, int startGun, Func<bool> isMacroOn, Action<int, Fit> onApply, Action<string> onPop)
        {
            names = gunNames;
            gun = Math.Clamp(startGun - 1, 0, names.Length - 1);
            macroOn = isMacroOn;
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
                Text = "1. เข้าห้องซ้อม ถือปืน + กล้องที่จะใช้จริง แล้ว**ปิดมาโคร** (ตอนเปิดมาโครจะไม่อัด)\n" +
                       "2. เล็งจุดหนึ่งบนกำแพง ยิงค้างจนเกือบหมดแม็ก แล้ว**ลากเมาส์ลงเอง**ให้เป้าอยู่จุดเดิมให้มากที่สุด\n" +
                       "3. ทำ 3–5 ชุด (ป้ายบนจอบอกทุกชุด) แล้วกลับมากด \"ใช้ค่านี้\" — ชุดที่ต่างจากชุดอื่นมากจะถูกตัดทิ้งเอง"
                       .Replace("**", "")
            };

            undoBtn = Btn("ลบชุดล่าสุด", () => { if (sprays.Count > 0) sprays.RemoveAt(sprays.Count - 1); Refresh(); }, 0);
            var clearBtn = Btn("เริ่มใหม่", () => { sprays.Clear(); Refresh(); }, 6);
            applyBtn = Btn("ใช้ค่านี้", Apply, 8);
            applyBtn.BorderBrush = new SolidColorBrush(Blue);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(undoBtn);
            left.Children.Add(clearBtn);
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

        void OnRaw(int dx, int dy, int left)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (left == 1 && !down)
            {
                down = true;
                t0 = now;
                cur = new List<(double, int, int)>();
                ignoreThis = macroOn();
                if (ignoreThis) pop("ปิดมาโครก่อนอัด (ชุดนี้ไม่นับ)");
            }
            if (down && (dx != 0 || dy != 0)) cur.Add((now - t0, dx, dy));
            if (left == -1 && down)
            {
                down = false;
                double ms = now - t0;
                if (ignoreThis) return;
                if (ms < MIN_MS) return;                        // แตะยิง / คลิกทั่วไป
                sprays.Add(Bucket(cur, ms));
                var f = Analyze(sprays);
                pop($"อัดชุดที่ {sprays.Count} ({ms / 1000:0.0} วิ)" + (f != null ? $" · base {GHub.Fmt(f.Base)} x {GHub.Fmt(f.X)}" : ""));
                Refresh();
            }
        }

        static Spray Bucket(List<(double t, int dx, int dy)> s, double ms)
        {
            int n = Math.Max(1, (int)(ms / 10));                 // ตัดช่วงสุดท้ายที่ไม่เต็ม 10 ms
            var y = new double[n];
            var x = new double[n];
            foreach (var (t, dx, dy) in s)
            {
                int k = (int)(t / 10);
                if (k >= n) continue;
                y[k] += dy;
                x[k] += dx;
            }
            return new Spray { Y = y, X = x, Ms = ms };
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

            var bars = LiveLink.Quantize(avg);
            double min = bars.Min(), max = bars.Max();
            double baseV = Math.Max(0, Math.Floor(min * 2) / 2);
            if (max - baseV > 31.75) baseV = Math.Ceiling((max - 31.75) * 2) / 2;   // กราฟส่งได้สูงสุด 31.75
            var steps = LiveLink.Quantize(bars.Select(v => Math.Clamp(v - baseV, 0, 31.75)).ToArray());
            double x = cntX > 0 ? Math.Round(sumX / cntX * 2) / 2 : 0;
            return new Fit { Base = baseV, X = x, Steps = steps, Avg = avg, Used = use.Count, Dropped = dropped };
        }

        void Apply()
        {
            var f = Analyze(sprays);
            if (f == null) return;
            apply(gun, f);
            status.Text = $"ใช้ค่ากับ {names[gun]} แล้ว — ลองยิงโดยเปิดมาโคร ถ้ายังดึงเกิน/ไม่พอ ปรับ base ด้วย Ctrl/Alt + Back";
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
