using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace GunBeep
{
    // กราฟแรงดีด (ตาราง segs) ของแต่ละปืน: แท่งละ 100 ms ลากเมาส์ระบายเพื่อปรับ
    // ค่าในกราฟ = แรงดึงลงเพิ่มจาก base ต่อทุก 10 ms  (แรงจริง = base + ค่าในกราฟ)
    public class SegsEditor : Window
    {
        const int BAR = 10;                         // 1 แท่ง = 10 ช่วง x 10 ms
        const double PW = 780, PH = 270, PADL = 40, PADB = 26, PADT = 12, PADR = 8;
        const double SNAP = 0.25;

        static readonly Color Blue = Color.FromRgb(0x1D, 0x6B, 0xFF), Ice = Color.FromRgb(0x5C, 0xE1, 0xFF);
        static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFF));
        static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x80, 0x94, 0xBC));
        static readonly Brush Grid = new SolidColorBrush(Color.FromRgb(0x18, 0x22, 0x3A));

        readonly double[][] steps, original;
        readonly string[] names;
        readonly double[] bases;
        readonly Canvas plot = new Canvas { Width = PW, Height = PH, Background = Brushes.Transparent, ClipToBounds = true };
        readonly TextBlock info = new TextBlock { FontSize = 12.5, Foreground = Muted, Margin = new Thickness(2, 8, 0, 0) };
        readonly Button[] tabs = new Button[3];
        readonly Button saveBtn;
        readonly Stack<(int gun, double[] st)> undo = new();
        readonly Style keyBtn;

        int gun, hover = -1, lastBar = -1;
        double lastVal, ymax = 6;
        bool painting;

        public double[][] Result { get; private set; }

        public SegsEditor(string[] gunNames, double[][] current, double[] baseVals, int startGun, bool live)
        {
            names = gunNames;
            bases = baseVals;
            var def = GHub.DefaultSteps();
            original = current.Select(s => (s ?? def).ToArray()).ToArray();
            steps = original.Select(s => s.ToArray()).ToArray();
            gun = Math.Clamp(startGun - 1, 0, steps.Length - 1);
            keyBtn = Application.Current.MainWindow?.TryFindResource("KeyBtn") as Style;

            Title = "กราฟแรงดีด";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI, Leelawadee UI");

            // หัว: ชื่อ + แท็บปืน
            var title = new TextBlock { Text = "กราฟแรงดีด", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Text, VerticalAlignment = VerticalAlignment.Center };
            var sub = new TextBlock
            {
                Text = "ลากเมาส์บนกราฟเพื่อปรับ · แรงดึงจริง = base + ค่าในกราฟ (ต่อ 10 ms) · หลังแท่งสุดท้ายใช้ค่าแท่งสุดท้ายไปตลอด",
                FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0)
            };
            var tabRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < steps.Length; i++)
            {
                int g = i;
                tabs[i] = Btn("", () => { gun = g; Rescale(); Redraw(); }, 6);
                tabRow.Children.Add(tabs[i]);
            }
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(tabRow, Dock.Right);
            head.Children.Add(tabRow);
            var titles = new StackPanel();
            titles.Children.Add(title);
            titles.Children.Add(sub);
            head.Children.Add(titles);

            // ปุ่มเครื่องมือ
            var tools = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            tools.Children.Add(Btn("ทั้งเส้น −0.5", () => Shift(-0.5), 0));
            tools.Children.Add(Btn("ทั้งเส้น +0.5", () => Shift(+0.5), 6));
            tools.Children.Add(Btn("เกลี่ยให้เรียบ", Smooth, 6));
            Button copyBtn = null;
            copyBtn = Btn("คัดลอกจากปืน...", () => CopyMenu(copyBtn), 6);
            tools.Children.Add(copyBtn);
            tools.Children.Add(Btn("ค่าเริ่มต้น", () => { Push(); steps[gun] = GHub.DefaultSteps(); Rescale(); Redraw(); }, 6));
            tools.Children.Add(Btn("ย้อนกลับ (Ctrl+Z)", Undo, 6));

            // ปุ่มล่าง
            saveBtn = Btn(live ? "บันทึก + ใช้เลย" : "บันทึก + รีสตาร์ท G Hub", Save, 8);
            saveBtn.BorderBrush = new SolidColorBrush(Blue);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            bottom.Children.Add(Btn("ยกเลิก", () => { DialogResult = false; }, 0));
            bottom.Children.Add(saveBtn);
            var note = new TextBlock
            {
                Text = live ? "ส่งเข้า G Hub แบบ real time (~2-10 วิ ไม่ต้องรีสตาร์ท) · ค่าปัดเป็นแท่งละ 0.1 วิ ละเอียด 0.25"
                            : "สคริปต์ใน G Hub ยังเป็นรุ่นเก่า — บันทึกแล้วต้องรีสตาร์ท G Hub (~10 วิ) · กด ตั้งค่าอัตโนมัติ เพื่ออัปเดต",
                FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center
            };
            var foot = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Right);
            foot.Children.Add(bottom);
            foot.Children.Add(note);

            var plotBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x16)), CornerRadius = new CornerRadius(12),
                BorderBrush = Grid, BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = plot
            };

            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(plotBox);
            stack.Children.Add(info);
            stack.Children.Add(tools);
            stack.Children.Add(foot);

            Content = new Border
            {
                Margin = new Thickness(18), Padding = new Thickness(22, 18, 22, 18), CornerRadius = new CornerRadius(20),
                Background = new LinearGradientBrush(Color.FromRgb(0x0E, 0x15, 0x28), Color.FromRgb(0x07, 0x0A, 0x12), 90),
                BorderBrush = new LinearGradientBrush(Ice, Color.FromRgb(0xC9, 0xA5, 0x4A), 45), BorderThickness = new Thickness(1.5),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.6 },
                Child = stack
            };

            plot.MouseLeftButtonDown += (s, e) => { Push(); painting = true; lastBar = -1; plot.CaptureMouse(); Paint(e.GetPosition(plot)); };
            plot.MouseMove += (s, e) =>
            {
                var p = e.GetPosition(plot);
                if (painting) Paint(p);
                else { int b = BarAt(p.X); if (b != hover) { hover = b; Redraw(); } }
            };
            plot.MouseLeftButtonUp += (s, e) => { painting = false; plot.ReleaseMouseCapture(); Redraw(); };
            plot.MouseLeave += (s, e) => { if (!painting) { hover = -1; Redraw(); } };
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed && !painting) try { DragMove(); } catch { } };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
                else if (e.Key == Key.Escape) DialogResult = false;
            };

            Rescale();
            Redraw();
        }

        Button Btn(string label, Action click, double left)
        {
            var b = new Button { Content = label, Margin = new Thickness(left, 0, 0, 0) };
            if (keyBtn != null) b.Style = keyBtn;
            b.Click += (s, e) => click();
            return b;
        }

        // ---------------------------------------------------------------- ข้อมูล

        int Bars => (steps[gun].Length + BAR - 1) / BAR;

        double BarValue(int b)
        {
            var st = steps[gun];
            int a = b * BAR, z = Math.Min(st.Length, a + BAR);
            double sum = 0;
            for (int i = a; i < z; i++) sum += st[i];
            return z > a ? sum / (z - a) : 0;
        }

        void SetBar(int b, double v)
        {
            var st = steps[gun];
            for (int i = b * BAR; i < Math.Min(st.Length, b * BAR + BAR); i++) st[i] = v;
        }

        void Push()
        {
            undo.Push((gun, steps[gun].ToArray()));
            if (undo.Count > 60) { var keep = undo.Take(40).Reverse().ToList(); undo.Clear(); keep.ForEach(undo.Push); }
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            var (g, st) = undo.Pop();
            steps[g] = st;
            gun = g;
            Rescale();
            Redraw();
        }

        void Shift(double d)
        {
            Push();
            var st = steps[gun];
            for (int i = 0; i < st.Length; i++) st[i] = Math.Max(0, Math.Round((st[i] + d) / SNAP) * SNAP);
            Rescale();
            Redraw();
        }

        void Smooth()
        {
            Push();
            int n = Bars;
            var v = Enumerable.Range(0, n).Select(BarValue).ToArray();
            for (int b = 0; b < n; b++)
            {
                double l = v[Math.Max(0, b - 1)], r = v[Math.Min(n - 1, b + 1)];
                SetBar(b, Math.Round((l + 2 * v[b] + r) / 4 / SNAP) * SNAP);
            }
            Redraw();
        }

        void CopyMenu(Button anchor)
        {
            var m = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            if (Application.Current.MainWindow?.TryFindResource(typeof(ContextMenu)) is Style ms) m.Style = ms;
            var itemStyle = Application.Current.MainWindow?.TryFindResource(typeof(MenuItem)) as Style;
            for (int i = 0; i < steps.Length; i++)
            {
                if (i == gun) continue;
                int src = i;
                var mi = new MenuItem { Header = $"ปืน {i + 1} · {names[i]}" };
                if (itemStyle != null) mi.Style = itemStyle;
                mi.Click += (s, e) => { Push(); steps[gun] = steps[src].ToArray(); Rescale(); Redraw(); };
                m.Items.Add(mi);
            }
            m.IsOpen = true;
        }

        void Save()
        {
            Result = steps.Select(s => s.ToArray()).ToArray();
            DialogResult = true;
        }

        bool Changed(int g) => !GHub.SameSteps(steps[g], original[g]);

        // ---------------------------------------------------------------- วาด

        double X0 => PADL;
        double BarW => (PW - PADL - PADR) / Bars;
        double Y(double v) => PH - PADB - v / ymax * (PH - PADB - PADT);
        double V(double y) => (PH - PADB - y) / (PH - PADB - PADT) * ymax;
        int BarAt(double x) { int b = (int)Math.Floor((x - X0) / BarW); return b >= 0 && b < Bars ? b : -1; }

        void Rescale()
        {
            double max = steps.SelectMany(s => s).DefaultIfEmpty(0).Max();
            ymax = Math.Min(31.75, Math.Max(6, Math.Ceiling(max + 2)));      // ส่งได้สูงสุด 31.75
        }

        void Paint(Point p)
        {
            int b = BarAt(Math.Clamp(p.X, X0, PW - PADR - 0.01));
            if (b < 0) return;
            double v = Math.Clamp(Math.Round(V(p.Y) / SNAP) * SNAP, 0, ymax);
            // ลากเร็วข้ามแท่ง: เติมแท่งระหว่างทางให้ต่อเนื่อง
            if (lastBar >= 0 && Math.Abs(b - lastBar) > 1)
            {
                int dir = Math.Sign(b - lastBar);
                for (int k = lastBar + dir; k != b; k += dir)
                {
                    double t = (double)(k - lastBar) / (b - lastBar);
                    SetBar(k, Math.Round((lastVal + (v - lastVal) * t) / SNAP) * SNAP);
                }
            }
            SetBar(b, v);
            lastBar = b;
            lastVal = v;
            hover = b;
            Redraw();
        }

        void Redraw()
        {
            plot.Children.Clear();
            int n = Bars;

            // เส้นตาราง + ตัวเลขแกน y
            double stepY = ymax <= 8 ? 1 : 2;
            for (double v = 0; v <= ymax + 1e-9; v += stepY)
            {
                double y = Y(v);
                plot.Children.Add(new Line { X1 = X0, X2 = PW - PADR, Y1 = y, Y2 = y, Stroke = Grid, StrokeThickness = 1 });
                Label(GHub.Fmt(v), 0, y - 8, 30, TextAlignment.Right);
            }
            // แกนเวลา ทุก 0.5 วิ
            for (int b = 0; b <= n; b += 5)
            {
                double x = X0 + b * BarW;
                plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = PADT, Y2 = PH - PADB, Stroke = Grid, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 4 } });
                Label(GHub.Fmt(b * BAR * 10 / 1000.0) + " วิ", x - 20, PH - PADB + 5, 40, TextAlignment.Center);
            }

            // แท่ง
            var fill = new LinearGradientBrush(Ice, Blue, 90);
            var fillHot = new LinearGradientBrush(Color.FromRgb(0xE6, 0xF7, 0xFF), Ice, 90);
            for (int b = 0; b < n; b++)
            {
                double v = BarValue(b), top = Y(v);
                var r = new Rectangle
                {
                    Width = Math.Max(2, BarW - 3), Height = Math.Max(1.5, PH - PADB - top),
                    RadiusX = 3, RadiusY = 3, Fill = b == hover ? fillHot : fill, Opacity = b == hover ? 1 : 0.85
                };
                Canvas.SetLeft(r, X0 + b * BarW + 1.5);
                Canvas.SetTop(r, PH - PADB - r.Height);
                plot.Children.Add(r);
            }

            // แท็บ + ข้อความ
            for (int i = 0; i < steps.Length; i++)
            {
                tabs[i].Content = $"{i + 1} · {names[i]}" + (Changed(i) ? " •" : "");
                if (i == gun) tabs[i].BorderBrush = new SolidColorBrush(Ice);
                else tabs[i].ClearValue(Control.BorderBrushProperty);
            }
            saveBtn.IsEnabled = Enumerable.Range(0, steps.Length).Any(Changed);

            if (hover >= 0)
            {
                double v = BarValue(hover);
                info.Text = $"{GHub.Fmt(hover * 0.1)}–{GHub.Fmt(hover * 0.1 + 0.1)} วิ :  กราฟ {GHub.Fmt(Math.Round(v, 2))}  +  base {GHub.Fmt(bases[gun])}  =  ดึงลง {GHub.Fmt(Math.Round(v + bases[gun], 2))} ต่อ 10 ms";
            }
            else
            {
                double avg = steps[gun].DefaultIfEmpty(0).Average();
                info.Text = $"ปืน {gun + 1} · {names[gun]} · base {GHub.Fmt(bases[gun])} · ยาว {GHub.Fmt(steps[gun].Length / 100.0)} วิ · เฉลี่ยในกราฟ {GHub.Fmt(Math.Round(avg, 2))}";
            }
        }

        void Label(string text, double x, double y, double w, TextAlignment align)
        {
            var t = new TextBlock { Text = text, FontSize = 10.5, Foreground = Muted, Width = w, TextAlignment = align };
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            plot.Children.Add(t);
        }

        public static double[][] Edit(Window owner, string[] names, double[][] current, double[] bases, int gun, bool live)
        {
            var d = new SegsEditor(names, current, bases, gun, live) { Owner = owner };
            return d.ShowDialog() == true ? d.Result : null;
        }
    }
}
