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
    // ตารางแรงดึงรายนัด: นัดที่ 1, 2, 3 ... ดึงลงกี่หน่วย (หน่วยเดียวกับ MoveMouseRelative ของ G Hub)
    //   เป็นอีกมุมมองของตาราง segs (แรงดึงต่อ 10 ms) — แปลงไปกลับแบบคิดตามเวลาจริง จึงได้ค่าตรงทุกนัด
    //   ค่าที่โชว์ = ดึงจริงต่อนัด (รวม base แล้ว) · หลังนัดสุดท้ายใช้แรงของนัดสุดท้ายต่อไป
    public class ShotEditor : Window
    {
        const double PW = 640, PH = 400, PADL = 40, PADB = 24, PADT = 12, PADR = 8;
        const int STEP_MS = 10;

        static readonly Color Blue = Color.FromRgb(0x1D, 0x6B, 0xFF), Ice = Color.FromRgb(0x5C, 0xE1, 0xFF), Gold = Color.FromRgb(0xF2, 0xA5, 0x16);
        static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFF));
        static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x80, 0x94, 0xBC));
        static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x22, 0x3A));
        static readonly Brush RowA = new SolidColorBrush(Color.FromRgb(0x0C, 0x13, 0x24)), RowB = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x2C));

        readonly string[] names;
        readonly double[] bases;
        readonly int[] ms, count;
        readonly double[][] shots, original;
        readonly int[] originalMs;
        readonly Canvas plot = new() { Width = PW, Height = PH, Background = Brushes.Transparent, ClipToBounds = true };
        readonly StackPanel rows = new();
        readonly ScrollViewer rowScroll;
        readonly TextBlock info = new() { FontSize = 12.5, Foreground = Muted, Margin = new Thickness(2, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        readonly TextBlock msText = new() { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Text, Width = 52, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock nText = new() { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Text, Width = 34, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly Button[] tabs = new Button[3];
        readonly Button[] magBtns = new Button[3];
        readonly Button saveBtn;
        readonly Stack<(int gun, double[] v, int n, int ms)> undo = new();
        readonly Style keyBtn;
        readonly List<TextBox> boxes = new();

        int gun, hover = -1, lastShot = -1;
        double lastVal, ymax = 60;
        bool painting, syncing;

        public double[][] ResultSteps { get; private set; }   // null = ไม่ได้แก้ชุดนั้น
        public int[] ResultMs => ms;
        public int[] ResultCount => count;

        // ---------------------------------------------------------------- แปลง segs ⇄ รายนัด

        // แรงดึงต่อนัด: รวมแรงทุก 10 ms ในช่วงเวลาของนัดนั้น (รวม base)
        public static double[] ToShots(double[] steps, double baseVal, int ms, int n)
        {
            var p = new double[n];
            if (steps == null || steps.Length == 0) return p;
            for (int k = 0; k < n; k++)
            {
                double a = k * ms, b = (k + 1) * ms, sum = 0;
                for (int j = (int)(a / STEP_MS); j * STEP_MS < b; j++)
                {
                    double lo = Math.Max(a, j * STEP_MS), hi = Math.Min(b, (j + 1) * STEP_MS);
                    if (hi <= lo) continue;
                    double v = steps[Math.Min(j, steps.Length - 1)] + baseVal;   // เกินตาราง = ค่าสุดท้าย (เหมือนสคริปต์)
                    sum += v * (hi - lo) / STEP_MS;
                }
                p[k] = Math.Round(sum, 1);
            }
            return p;
        }

        // กลับเป็นแรงต่อ 10 ms (ไม่รวม base) ให้รวมแต่ละนัดได้ตรงเป๊ะ
        //   ช่วง 10 ms ที่คร่อมสองนัด = ผสมแรงสองนัดตามสัดส่วนเวลา
        //   ช่วงที่อยู่ในนัดเดียวทั้งช่วง = ค่าที่ทำให้ผลรวมของนัดนั้นตรงพอดี (ชดเชยส่วนที่ช่วงคร่อมกินไป)
        //   ช่วงสุดท้ายคร่อมจุดจบตาราง = แรงนัดสุดท้าย (สคริปต์ใช้ค่าสุดท้ายต่อไปเรื่อยๆ)
        public static double[] FromShots(double[] p, double baseVal, int ms)
        {
            int n = p.Length;
            int len = (int)Math.Ceiling(n * ms / (double)STEP_MS);
            var v = new double?[len];
            double Rate(int k) => p[Math.Min(k, n - 1)] / ms * STEP_MS;
            for (int j = 0; j < len; j++)
            {
                double a = j * STEP_MS, b = a + STEP_MS;
                int ka = (int)(a / ms), kb = (int)((b - 1e-9) / ms);
                if (ka != kb) v[j] = (Rate(ka) * ((ka + 1) * ms - a) + Rate(kb) * (b - (ka + 1) * ms)) / STEP_MS;
            }
            for (int k = 0; k < n; k++)
            {
                double a = k * ms, b = (k + 1) * ms, fixedSum = 0;
                var inner = new List<int>();
                for (int j = (int)(a / STEP_MS); j < len && j * STEP_MS < b; j++)
                {
                    double lo = Math.Max(a, j * STEP_MS), hi = Math.Min(b, (j + 1) * STEP_MS);
                    if (v[j] == null) inner.Add(j);
                    else fixedSum += v[j].Value * (hi - lo) / STEP_MS;
                }
                foreach (int j in inner) v[j] = (p[k] - fixedSum) / inner.Count;
            }
            return v.Select(x => Math.Round((x ?? 0) - baseVal, 3)).ToArray();
        }

        // ---------------------------------------------------------------- หน้าต่าง

        public ShotEditor(string[] gunNames, double[][] steps, double[] baseVals, int[] shotMs, int[] shotCount, int startGun)
        {
            names = gunNames;
            bases = baseVals;
            int g3 = steps.Length;
            ms = Enumerable.Range(0, g3).Select(i => Math.Clamp(shotMs != null && i < shotMs.Length ? shotMs[i] : 86, 40, 250)).ToArray();
            count = Enumerable.Range(0, g3).Select(i => Math.Clamp(shotCount != null && i < shotCount.Length ? shotCount[i] : 42, 5, 120)).ToArray();
            originalMs = ms.ToArray();
            var def = GHub.DefaultSteps();
            original = Enumerable.Range(0, g3).Select(i => ToShots(steps[i] ?? def, bases[i], ms[i], count[i])).ToArray();
            shots = original.Select(s => s.ToArray()).ToArray();
            gun = Math.Clamp(startGun - 1, 0, g3 - 1);
            keyBtn = Application.Current.MainWindow?.TryFindResource("KeyBtn") as Style;

            Title = "ตารางแรงดึงรายนัด";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI, Leelawadee UI");

            // หัว: ชื่อ + แท็บชุดค่า
            var title = new TextBlock { Text = "ตารางแรงดึงรายนัด", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Text };
            var sub = new TextBlock
            {
                Text = "แต่ละนัดดึงลงกี่หน่วย (รวม base แล้ว) · แก้ในช่อง / กด − + / ลากเมาส์บนกราฟ · หลังนัดสุดท้ายใช้แรงนัดสุดท้ายไปตลอด",
                FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0)
            };
            var tabRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < g3; i++)
            {
                int g = i;
                tabs[i] = Btn("", () => { gun = g; BuildRows(); Rescale(); Redraw(); }, 6);
                tabRow.Children.Add(tabs[i]);
            }
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(tabRow, Dock.Right);
            head.Children.Add(tabRow);
            var titles = new StackPanel();
            titles.Children.Add(title);
            titles.Children.Add(sub);
            head.Children.Add(titles);

            // แถวตั้งค่า: ความเร็วยิง, จำนวนนัด
            var bar1 = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            bar1.Children.Add(Lbl("ความเร็วยิง"));
            bar1.Children.Add(Btn("−", () => SetMs(ms[gun] - 1), 6));
            bar1.Children.Add(msText);
            bar1.Children.Add(Btn("+", () => SetMs(ms[gun] + 1), 0));
            bar1.Children.Add(Lbl("ms/นัด", 4));
            bar1.Children.Add(Lbl("จำนวนนัด", 22));
            int[] mags = { 30, 40, 42 };
            string[] magNames = { "แม็กปกติ 30", "ขยาย 40", "40 + เผื่อ 2" };
            for (int i = 0; i < 3; i++)
            {
                int m = mags[i];
                magBtns[i] = Btn(magNames[i], () => SetCount(m), i == 0 ? 6 : 4);
                bar1.Children.Add(magBtns[i]);
            }
            bar1.Children.Add(Btn("−", () => SetCount(count[gun] - 1), 10));
            bar1.Children.Add(nText);
            bar1.Children.Add(Btn("+", () => SetCount(count[gun] + 1), 0));

            // แถวปรับทีละช่วง
            var bar2 = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            bar2.Children.Add(Lbl("ปรับทีละช่วง"));
            foreach (var (label, from, to) in new (string, double, double)[] { ("ทั้งหมด", 0, 1), ("ช่วงต้น", 0, 1 / 3.0), ("ช่วงกลาง", 1 / 3.0, 2 / 3.0), ("ช่วงท้าย", 2 / 3.0, 1) })
            {
                double f = from, t = to;
                bar2.Children.Add(Lbl(label, 14));
                bar2.Children.Add(Btn("−", () => AddRange(f, t, -1), 6));
                bar2.Children.Add(Btn("+", () => AddRange(f, t, +1), 3));
            }
            bar2.Children.Add(Btn("เกลี่ยให้เรียบ", Smooth, 18));
            bar2.Children.Add(Btn("ค่าเดิม", () => { Push(); shots[gun] = original[gun].ToArray(); count[gun] = shots[gun].Length; ms[gun] = originalMs[gun]; BuildRows(); Rescale(); Redraw(); }, 6));
            bar2.Children.Add(Btn("ย้อนกลับ (Ctrl+Z)", Undo, 6));

            // ตาราง (ซ้าย) + กราฟ (ขวา)
            rowScroll = new ScrollViewer { Content = rows, Height = PH + 16, Width = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var listBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x16)), CornerRadius = new CornerRadius(12),
                BorderBrush = GridBrush, BorderThickness = new Thickness(1), Padding = new Thickness(6), Child = rowScroll
            };
            var plotBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x16)), CornerRadius = new CornerRadius(12),
                BorderBrush = GridBrush, BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = plot, Margin = new Thickness(10, 0, 0, 0)
            };
            var body = new StackPanel { Orientation = Orientation.Horizontal };
            body.Children.Add(listBox);
            body.Children.Add(plotBox);

            // ปุ่มล่าง
            saveBtn = Btn("บันทึก + รีสตาร์ท G Hub", Save, 8);
            saveBtn.BorderBrush = new SolidColorBrush(Blue);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            bottom.Children.Add(Btn("ยกเลิก", () => { DialogResult = false; }, 0));
            bottom.Children.Add(saveBtn);
            var note = new TextBlock
            {
                Text = "บันทึกแล้ว G Hub จะรีสตาร์ท (~10 วิ) เพื่อโหลดตารางใหม่แบบละเอียดทุกนัด — อย่ากดตอนกำลังยิงกันอยู่",
                FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 560
            };
            var foot = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Right);
            foot.Children.Add(bottom);
            foot.Children.Add(note);

            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(bar1);
            stack.Children.Add(bar2);
            stack.Children.Add(body);
            stack.Children.Add(info);
            stack.Children.Add(foot);

            Content = new Border
            {
                Margin = new Thickness(18), Padding = new Thickness(22, 18, 22, 18), CornerRadius = new CornerRadius(20),
                Background = new LinearGradientBrush(Color.FromRgb(0x0E, 0x15, 0x28), Color.FromRgb(0x07, 0x0A, 0x12), 90),
                BorderBrush = new LinearGradientBrush(Ice, Color.FromRgb(0xC9, 0xA5, 0x4A), 45), BorderThickness = new Thickness(1.5),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.6 },
                Child = stack
            };

            plot.MouseLeftButtonDown += (s, e) => { Push(); painting = true; lastShot = -1; plot.CaptureMouse(); Paint(e.GetPosition(plot)); e.Handled = true; };
            plot.MouseMove += (s, e) =>
            {
                var p = e.GetPosition(plot);
                if (painting) Paint(p);
                else { int k = ShotAt(p.X); if (k != hover) { hover = k; Redraw(); } }
            };
            plot.MouseLeftButtonUp += (s, e) => { painting = false; plot.ReleaseMouseCapture(); BuildRowsValues(); Redraw(); };
            plot.MouseLeave += (s, e) => { if (!painting) { hover = -1; Redraw(); } };
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed && !painting && e.OriginalSource is not TextBox) try { DragMove(); } catch { } };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
                else if (e.Key == Key.Escape) DialogResult = false;
            };

            BuildRows();
            Rescale();
            Redraw();
        }

        Button Btn(string label, Action click, double left)
        {
            var b = new Button { Content = label, Margin = new Thickness(left, 0, 0, 0), MinWidth = 0 };
            if (keyBtn != null) b.Style = keyBtn;
            b.Padding = new Thickness(9, 4, 9, 4);
            b.Click += (s, e) => click();
            return b;
        }

        static TextBlock Lbl(string t, double left = 0) =>
            new() { Text = t, FontSize = 12.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 0, 0) };

        // ---------------------------------------------------------------- แก้ค่า

        void Push()
        {
            undo.Push((gun, shots[gun].ToArray(), count[gun], ms[gun]));
            if (undo.Count > 80) { var keep = undo.Take(60).Reverse().ToList(); undo.Clear(); keep.ForEach(undo.Push); }
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            var (g, v, n, m) = undo.Pop();
            gun = g;
            shots[g] = v;
            count[g] = n;
            ms[g] = m;
            BuildRows();
            Rescale();
            Redraw();
        }

        static double Clean(double v) => Math.Round(Math.Clamp(v, -50, 999), 1);

        void SetShot(int k, double v)
        {
            shots[gun][k] = Clean(v);
        }

        // ความเร็วยิงเปลี่ยน: แรงต่อนัดเท่าเดิม (สคริปต์จะดึงเร็วขึ้น/ช้าลงตามจังหวะยิง)
        void SetMs(int v)
        {
            v = Math.Clamp(v, 40, 250);
            if (v == ms[gun]) return;
            Push();
            ms[gun] = v;
            Redraw();
        }

        void SetCount(int n)
        {
            n = Math.Clamp(n, 5, 120);
            if (n == count[gun]) return;
            Push();
            var old = shots[gun];
            var p = new double[n];
            for (int k = 0; k < n; k++) p[k] = k < old.Length ? old[k] : old[^1];   // เพิ่มนัด = ใช้แรงนัดสุดท้าย
            shots[gun] = p;
            count[gun] = n;
            BuildRows();
            Rescale();
            Redraw();
        }

        void AddRange(double from, double to, double d)
        {
            Push();
            int n = count[gun];
            int a = (int)Math.Round(from * n), b = (int)Math.Round(to * n);
            for (int k = a; k < b; k++) SetShot(k, shots[gun][k] + d);
            BuildRowsValues();
            Rescale();
            Redraw();
        }

        void Smooth()
        {
            Push();
            var v = shots[gun].ToArray();
            int n = v.Length;
            for (int k = 0; k < n; k++)
                SetShot(k, (v[Math.Max(0, k - 1)] + 2 * v[k] + v[Math.Min(n - 1, k + 1)]) / 4);
            BuildRowsValues();
            Redraw();
        }

        void Save()
        {
            ResultSteps = new double[shots.Length][];
            for (int i = 0; i < shots.Length; i++)
                if (Changed(i)) ResultSteps[i] = FromShots(shots[i], bases[i], ms[i]);
            DialogResult = true;
        }

        bool Changed(int g) => ms[g] != originalMs[g] || shots[g].Length != original[g].Length
                               || shots[g].Zip(original[g]).Any(p => Math.Abs(p.First - p.Second) > 1e-9);

        // ---------------------------------------------------------------- ตารางซ้าย

        void BuildRows()
        {
            syncing = true;                       // ช่องเก่าหลุดโฟกัสตอนล้าง → ห้าม Commit ค่าลงชุดใหม่
            Keyboard.ClearFocus();
            rows.Children.Clear();
            boxes.Clear();
            for (int k = 0; k < count[gun]; k++)
            {
                int idx = k;
                var row = new DockPanel { Background = k % 2 == 0 ? RowA : RowB, Height = 28, LastChildFill = false };
                var num = new TextBlock
                {
                    Text = (k + 1).ToString("00"), Width = 34, Foreground = Muted, FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center
                };
                var minus = Btn("−", () => { Push(); SetShot(idx, shots[gun][idx] - 1); BuildRowsValues(); Redraw(); }, 0);
                var plus = Btn("+", () => { Push(); SetShot(idx, shots[gun][idx] + 1); BuildRowsValues(); Redraw(); }, 0);
                minus.Padding = plus.Padding = new Thickness(8, 1, 8, 1);
                var box = new TextBox
                {
                    Width = 70, Margin = new Thickness(6, 2, 6, 2), FontSize = 13, FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Ice),
                    Background = new SolidColorBrush(Color.FromRgb(0x12, 0x1B, 0x30)), BorderBrush = GridBrush,
                    CaretBrush = new SolidColorBrush(Ice), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
                };
                void Commit()
                {
                    if (syncing || !boxes.Contains(box) || idx >= shots[gun].Length) return;
                    if (double.TryParse(box.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
                        && Math.Abs(Clean(v) - shots[gun][idx]) > 1e-9)
                    {
                        Push();
                        SetShot(idx, v);
                        Rescale();
                    }
                    BuildRowsValues();
                    Redraw();
                }
                box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter || e.Key == Key.Down || e.Key == Key.Up)
                    {
                        Commit();
                        int next = idx + (e.Key == Key.Up ? -1 : 1);
                        if (next >= 0 && next < boxes.Count) { boxes[next].Focus(); boxes[next].SelectAll(); }
                        e.Handled = true;
                    }
                };
                box.LostKeyboardFocus += (s, e) => Commit();
                box.GotKeyboardFocus += (s, e) => { hover = idx; Redraw(); };
                boxes.Add(box);
                DockPanel.SetDock(num, Dock.Left);
                row.Children.Add(num);
                row.Children.Add(minus);
                row.Children.Add(box);
                row.Children.Add(plus);
                rows.Children.Add(row);
            }
            BuildRowsValues();
        }

        void BuildRowsValues()
        {
            syncing = true;
            for (int k = 0; k < boxes.Count && k < shots[gun].Length; k++)
                if (!boxes[k].IsKeyboardFocused) boxes[k].Text = GHub.Fmt(shots[gun][k]);
            syncing = false;
        }

        // ---------------------------------------------------------------- กราฟขวา

        double X0 => PADL;
        double BarW => (PW - PADL - PADR) / count[gun];
        double Y(double v) => PH - PADB - v / ymax * (PH - PADB - PADT);
        double V(double y) => (PH - PADB - y) / (PH - PADB - PADT) * ymax;
        int ShotAt(double x) { int k = (int)Math.Floor((x - X0) / BarW); return k >= 0 && k < count[gun] ? k : -1; }

        void Rescale()
        {
            double max = shots[gun].DefaultIfEmpty(0).Max();
            ymax = Math.Max(10, Math.Ceiling((max * 1.15 + 1) / 10) * 10);
        }

        void Paint(Point p)
        {
            int k = ShotAt(Math.Clamp(p.X, X0, PW - PADR - 0.01));
            if (k < 0) return;
            double v = Clean(Math.Round(Math.Clamp(V(p.Y), 0, ymax)));
            if (lastShot >= 0 && Math.Abs(k - lastShot) > 1)
            {
                int dir = Math.Sign(k - lastShot);
                for (int j = lastShot + dir; j != k; j += dir)
                    SetShot(j, Math.Round(lastVal + (v - lastVal) * (j - lastShot) / (double)(k - lastShot)));
            }
            SetShot(k, v);
            lastShot = k;
            lastVal = v;
            hover = k;
            Redraw();
        }

        void Redraw()
        {
            plot.Children.Clear();
            int n = count[gun];
            var p = shots[gun];
            msText.Text = ms[gun].ToString();
            nText.Text = n.ToString();
            for (int i = 0; i < 3; i++)
                magBtns[i].BorderBrush = (new[] { 30, 40, 42 })[i] == n ? new SolidColorBrush(Gold) : (Brush)new SolidColorBrush(Color.FromRgb(0x24, 0x34, 0x5A));

            // เส้นตาราง
            double stepY = ymax <= 40 ? 5 : ymax <= 100 ? 10 : 20;
            for (double v = 0; v <= ymax + 1e-9; v += stepY)
            {
                double y = Y(v);
                plot.Children.Add(new Line { X1 = X0, X2 = PW - PADR, Y1 = y, Y2 = y, Stroke = GridBrush, StrokeThickness = 1 });
                Label(GHub.Fmt(v), 0, y - 8, 32, TextAlignment.Right);
            }
            // แบ่งช่วงต้น/กลาง/ท้าย + เลขนัดทุก 5
            foreach (double f in new[] { 1 / 3.0, 2 / 3.0 })
            {
                double x = X0 + Math.Round(f * n) * BarW;
                plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = PADT, Y2 = PH - PADB, Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xC9, 0xA5, 0x4A)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 } });
            }
            for (int k = 0; k < n; k += 5)
                Label((k + 1).ToString(), X0 + k * BarW + BarW / 2 - 12, PH - PADB + 5, 24, TextAlignment.Center);

            // แท่งรายนัด + จุด
            var fill = new LinearGradientBrush(Ice, Blue, 90);
            var fillHot = new LinearGradientBrush(Color.FromRgb(0xFF, 0xE3, 0xA8), Gold, 90);
            Point? prev = null;
            for (int k = 0; k < n; k++)
            {
                double top = Y(Math.Max(0, p[k]));
                var r = new Rectangle
                {
                    Width = Math.Max(2, BarW - 2), Height = Math.Max(1.5, PH - PADB - top),
                    RadiusX = 2, RadiusY = 2, Fill = k == hover ? fillHot : fill, Opacity = k == hover ? 1 : 0.55
                };
                Canvas.SetLeft(r, X0 + k * BarW + 1);
                Canvas.SetTop(r, PH - PADB - r.Height);
                plot.Children.Add(r);
                var c = new Point(X0 + k * BarW + BarW / 2, top);
                if (prev != null) plot.Children.Add(new Line { X1 = prev.Value.X, Y1 = prev.Value.Y, X2 = c.X, Y2 = c.Y, Stroke = new SolidColorBrush(Ice), StrokeThickness = 1.5 });
                prev = c;
            }

            // แท็บ + ข้อความ
            for (int i = 0; i < shots.Length; i++)
            {
                tabs[i].Content = $"{i + 1} · {names[i]}" + (Changed(i) ? " •" : "");
                if (i == gun) tabs[i].BorderBrush = new SolidColorBrush(Ice);
                else tabs[i].ClearValue(Control.BorderBrushProperty);
            }
            saveBtn.IsEnabled = Enumerable.Range(0, shots.Length).Any(Changed);

            double total = p.Sum();
            string rpm = $"{60000 / ms[gun]:0} นัด/นาที";
            info.Text = hover >= 0 && hover < n
                ? $"นัดที่ {hover + 1} ({hover * ms[gun]}–{(hover + 1) * ms[gun]} ms) : ดึงลง {GHub.Fmt(p[hover])} หน่วย · base {GHub.Fmt(bases[gun])} อยู่ในค่านี้แล้ว"
                : $"{names[gun]} · {n} นัด · {ms[gun]} ms/นัด ({rpm}) · ดึงรวม {GHub.Fmt(Math.Round(total))} หน่วย · สูงสุด {GHub.Fmt(p.DefaultIfEmpty(0).Max())} ต่อนัด";
        }

        void Label(string text, double x, double y, double w, TextAlignment align)
        {
            var t = new TextBlock { Text = text, FontSize = 10.5, Foreground = Muted, Width = w, TextAlignment = align };
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            plot.Children.Add(t);
        }

        public static ShotEditor Edit(Window owner, string[] names, double[][] steps, double[] bases, int[] shotMs, int[] shotCount, int gun)
        {
            var d = new ShotEditor(names, steps, bases, shotMs, shotCount, gun) { Owner = owner };
            return d.ShowDialog() == true ? d : null;
        }
    }
}
