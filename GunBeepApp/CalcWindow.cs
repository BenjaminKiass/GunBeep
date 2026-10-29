using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GunBeep
{
    // ตั้งค่าตัวคูณแรงดีด: ของแต่ง / ท่าทาง / ปืน — สโคปคำนวณเองจากความไวในไฟล์ตั้งค่า PUBG (โชว์ให้ดู)
    public sealed class CalcWindow : Window
    {
        static readonly Brush Bg = Frozen("#0E1628"), Card = Frozen("#16213A"), Line = Frozen("#2A3A5C"),
                              Text = Frozen("#E6EDF8"), Muted = Frozen("#8A9BB8"), Accent = Frozen("#1D6BFF"), Ice = Frozen("#8FDBFF");
        static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        readonly Settings cfg;
        readonly Action changed;
        readonly Button power = new();
        readonly StackPanel guns = new();
        readonly TextBlock example = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        CalcSettings C => cfg.Calc ??= new CalcSettings();

        public CalcWindow(Settings cfg, Action changed)
        {
            this.cfg = cfg;
            this.changed = changed;
            Title = "คำนวณแรงดีด";
            Width = 640;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Bg;
            FontFamily = new FontFamily("Segoe UI, Leelawadee UI");
            FontSize = 13;
            Foreground = Text;

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock { Text = "คำนวณแรงดีดอัตโนมัติ", FontSize = 16, FontWeight = FontWeights.Bold });
            root.Children.Add(new TextBlock
            {
                Text = "แรงดึง = ตารางของการ์ดชุดค่า × ปืน × สโคป × ของแต่ง × ท่าทาง — GunBeep คำนวณตัวคูณให้เองทุกครั้งที่เปิดกระเป๋า\n" +
                       "ตัวเลข % เป็นค่าเริ่มต้นโดยประมาณ (ไม่ใช่ค่าทางการของเกม) ลองยิงแล้วปรับได้",
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
            });
            power.Click += (s, e) => { C.On = !C.On; Save(); };
            root.Children.Add(power);

            root.Children.Add(Header("ของแต่ง (ลดแรงดีดกี่ %)"));
            root.Children.Add(Row("ปากกระบอก (คอมเพน/เบรก/ซ่อนไฟ)", () => C.MuzzlePct, v => C.MuzzlePct = v, () => C.RefMuzzle, v => C.RefMuzzle = v));
            root.Children.Add(Row("กริป", () => C.GripPct, v => C.GripPct = v, () => C.RefGrip, v => C.RefGrip = v));
            root.Children.Add(Row("พานท้าย", () => C.StockPct, v => C.StockPct = v, () => C.RefStock, v => C.RefStock = v));
            root.Children.Add(new TextBlock
            {
                Text = "✓ \"ตอนจูน\" = ตารางในการ์ดจูนไว้ตอนติดของแต่งชิ้นนั้น (ติดเหมือนตอนจูน = ×1, ไม่ได้ติด = ดึงแรงขึ้น)",
                Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
            });

            root.Children.Add(Header("ท่าทาง (ดูปุ่ม ย่อ/หมอบ ในเกม)"));
            root.Children.Add(Row("ย่อ", () => C.CrouchPct, v => C.CrouchPct = v));
            root.Children.Add(Row("หมอบ", () => C.PronePct, v => C.PronePct = v));

            root.Children.Add(Header("สโคป (คำนวณจากความไวในเกม — ไม่ต้องตั้ง)"));
            root.Children.Add(ScopeTable());

            root.Children.Add(Header("ปืน: มีค่าแรงดีดในตัวทุกปืนแล้ว (เทียบปืนที่ใช้จูนตาราง: " + (C.RefGun ?? "AUG") + ") — ตรงนี้คือปรับเพิ่ม ×1 = ตามค่าในตัว"));
            root.Children.Add(guns);
            root.Children.Add(example);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 60 };
            Refresh();
        }

        static TextBlock Header(string t) => new() { Text = t, Foreground = Muted, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

        static void Look(Button b)
        {
            b.Background = Card;
            b.Foreground = Text;
            b.BorderBrush = Line;
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(10, 6, 10, 6);
            b.Cursor = Cursors.Hand;
            b.Focusable = false;
        }

        Button Btn(string text, Action click)
        {
            var b = new Button { Content = text, MinWidth = 30 };
            Look(b);
            b.Padding = new Thickness(8, 2, 8, 2);
            b.Click += (s, e) => click();
            return b;
        }

        // แถว: ชื่อ · [−] ค่า% [+] · (ติดตอนจูน)
        FrameworkElement Row(string label, Func<double> get, Action<double> set, Func<bool> refGet = null, Action<bool> refSet = null, double step = 1, string unit = "%")
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var val = new TextBlock { Width = 64, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Ice, FontWeight = FontWeights.Bold };
            void Show() => val.Text = unit == "%" ? $"{get():+0;-0;0}%" : $"×{get():0.00}";
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(Btn("−", () => { set(Math.Round(get() - step, 2)); Show(); Save(); }));
            right.Children.Add(val);
            right.Children.Add(Btn("+", () => { set(Math.Round(get() + step, 2)); Show(); Save(); }));
            if (refGet != null)
            {
                var cb = new CheckBox { Content = "ตอนจูน", IsChecked = refGet(), Foreground = Text, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                cb.Click += (s, e) => { refSet(cb.IsChecked == true); Save(); };
                right.Children.Add(cb);
            }
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Show();
            return row;
        }

        FrameworkElement ScopeTable()
        {
            var names = cfg.Names;
            var grid = new Grid();
            for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(90) : new GridLength(1, GridUnitType.Star) });
            void Cell(int r, int c, string t, Brush b = null)
            {
                var tb = new TextBlock { Text = t, Foreground = b ?? Text, Margin = new Thickness(0, 1, 0, 1) };
                Grid.SetRow(tb, r); Grid.SetColumn(tb, c); grid.Children.Add(tb);
            }
            grid.RowDefinitions.Add(new RowDefinition());
            Cell(0, 0, "สโคป", Muted); Cell(0, 1, "ความไวในเกม", Muted); Cell(0, 2, "ใช้การ์ด", Muted); Cell(0, 3, "ตัวคูณ", Muted);
            int r = 1;
            foreach (var sc in RecoilCalc.Scopes)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                var (set, mult, _) = RecoilCalc.ForScope(sc.label, names);
                string sens = RecoilCalc.Sens.TryGetValue(sc.sensName, out double v) ? v.ToString("0.#") : "?";
                Cell(r, 0, sc.label);
                Cell(r, 1, sens);
                Cell(r, 2, set >= 1 ? $"{set} · {names[set - 1]}" : "—");
                Cell(r, 3, set >= 1 ? $"×{mult:0.00}" : "—", Ice);
                r++;
            }
            return grid;
        }

        void Refresh()
        {
            power.Content = C.On ? "● เปิดอยู่ — คลิกเพื่อปิด" : "○ ปิดอยู่ — คลิกเพื่อเปิด";
            Look(power);
            power.FontWeight = FontWeights.Bold;
            if (C.On) { power.BorderBrush = Accent; power.Foreground = Frozen("#5CE1FF"); }

            guns.Children.Clear();
            var keys = C.Guns.Keys.OrderBy(k => k).ToList();
            if (keys.Count == 0) guns.Children.Add(new TextBlock { Text = "ยังไม่เจอปืน — เปิดกระเป๋าในเกมแล้วจะขึ้นเอง", Foreground = Muted });
            foreach (var k in keys)
            {
                string key = k;
                guns.Children.Add(Row(key, () => C.Guns[key], v => C.Guns[key] = Math.Clamp(v, 0.3, 3), step: 0.05, unit: "x"));
            }
            var ex = RecoilCalc.Gear(C, null, 1, new[] { false, false, false, false });
            example.Text = $"ตัวอย่าง: ปืนไม่มีของแต่งเลย ยืนยิง = ×{ex.mult:0.00} ของตาราง";
        }

        void Save()
        {
            cfg.Save();
            changed();
            Refresh();
        }
    }
}
