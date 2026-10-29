using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace GunBeep
{
    // ตั้งตำแหน่งเก็บของเร็ว: ลากจุดบนภาพหน้ากระเป๋า (ภาพที่ผู้ใช้แคปเอง) — ช่องแรก, ช่องถัดไป, กระเป๋า (ปล่อย), จุดกลับ
    public sealed class LootWindow : Window
    {
        static readonly Brush Bg = Frozen("#0E1628"), Card = Frozen("#16213A"), Line = Frozen("#2A3A5C"),
                              Text = Frozen("#E6EDF8"), Muted = Frozen("#8A9BB8"), Accent = Frozen("#1D6BFF"), Gold = Frozen("#C9A54A"),
                              Orange = Frozen("#F2A516"), Green = Frozen("#19C37D"), Cyan = Frozen("#22C3EE");
        static readonly int[] SlotChoices = { 3, 5, 8, 10, 15 };
        static readonly string[] SpeedNames = { "ช้า", "กลาง", "เร็ว" };

        static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        public static string RefImagePath => System.IO.Path.Combine(Settings.KitDir, "loot-ref.png");

        readonly Settings cfg;
        readonly Action changed;
        readonly Func<int, string> keyName;
        readonly Func<int, bool> keyTaken;
        readonly Grid stage = new();                    // ขนาดเท่าภาพ (พิกเซล) อยู่ใน Viewbox
        readonly Image shot = new() { Stretch = Stretch.Fill };
        readonly Canvas canvas = new();
        readonly TextBlock hint = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        readonly Button keyBtn = new(), power = new();
        readonly Button[] slotBtns = new Button[SlotChoices.Length], speedBtns = new Button[3];
        double W = 1920, H = 1080;
        bool capturing;
        public bool Capturing => capturing;
        string dragging;                                // "start" "step" "drop" "center"

        public LootWindow(Settings cfg, Func<int, string> keyName, Func<int, bool> keyTaken, Action changed)
        {
            this.cfg = cfg;
            this.keyName = keyName;
            this.keyTaken = keyTaken;
            this.changed = changed;
            Title = "เก็บของเร็ว — ตั้งตำแหน่ง";
            Width = 1000;
            Height = 760;
            MinWidth = 700;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Bg;
            FontFamily = new FontFamily("Segoe UI, Leelawadee UI");
            FontSize = 13;
            Foreground = Text;

            var root = new DockPanel { Margin = new Thickness(18) };

            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = "เก็บของเร็ว (Fast loot)", FontSize = 16, FontWeight = FontWeights.Bold });
            head.Children.Add(new TextBlock
            {
                Text = "เปิดกระเป๋า (Tab) แล้วกดปุ่มที่ตั้งไว้ → ลากของบนพื้นเข้ากระเป๋าให้ทีละช่อง จากล่างขึ้นบน · กดซ้ำระหว่างเก็บ = หยุด\n" +
                       "ลากจุดบนภาพให้ตรงช่อง: ① ช่องแรก, ② ช่องที่สอง (บอกระยะห่าง), กระเป๋า = ที่ปล่อยของ, กลาง = เมาส์กลับมาที่นี่ตอนเสร็จ",
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
            });

            var row1 = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            power.Click += (s, e) => { if (cfg.LootKey != 0) { cfg.LootKey = 0; Save(); } else StartCapture(); };
            row1.Children.Add(power);
            keyBtn.Margin = new Thickness(6, 0, 0, 0);
            keyBtn.Click += (s, e) => StartCapture();
            row1.Children.Add(keyBtn);
            var paste = MakeButton("วางภาพจากคลิปบอร์ด");
            paste.Margin = new Thickness(18, 0, 0, 0);
            paste.ToolTip = "ในเกมเปิดกระเป๋าแล้วกด PrintScreen (หรือ Win+Shift+S เลือกทั้งจอ) แล้วกดปุ่มนี้";
            paste.Click += (s, e) => PasteImage();
            row1.Children.Add(paste);
            var open = MakeButton("เลือกไฟล์ภาพ...");
            open.Margin = new Thickness(6, 0, 0, 0);
            open.Click += (s, e) => OpenImage();
            row1.Children.Add(open);
            var reset = MakeButton("คืนค่าเริ่มต้น");
            reset.Margin = new Thickness(6, 0, 0, 0);
            reset.Click += (s, e) =>
            {
                var d = new Settings();
                cfg.LootStart = d.LootStart; cfg.LootStep = d.LootStep; cfg.LootDrop = d.LootDrop; cfg.LootCenter = d.LootCenter;
                Save();
            };
            row1.Children.Add(reset);
            head.Children.Add(row1);

            var row2 = new WrapPanel();
            row2.Children.Add(new TextBlock { Text = "ช่องต่อครั้ง", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            for (int i = 0; i < SlotChoices.Length; i++)
            {
                int n = SlotChoices[i];
                slotBtns[i] = MakeButton(n.ToString());
                slotBtns[i].MinWidth = 40;
                slotBtns[i].Margin = new Thickness(0, 0, 4, 0);
                slotBtns[i].Click += (s, e) => { cfg.LootSlots = n; Save(); };
                row2.Children.Add(slotBtns[i]);
            }
            row2.Children.Add(new TextBlock { Text = "ความเร็ว", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
            for (int i = 0; i < 3; i++)
            {
                int sp = i;
                speedBtns[i] = MakeButton(SpeedNames[i]);
                speedBtns[i].Margin = new Thickness(0, 0, 4, 0);
                speedBtns[i].ToolTip = "เก็บไม่ติดบางชิ้น = ลองช้าลง";
                speedBtns[i].Click += (s, e) => { cfg.LootSpeed = sp; Save(); };
                row2.Children.Add(speedBtns[i]);
            }
            head.Children.Add(row2);
            head.Children.Add(hint);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            stage.Children.Add(new Rectangle { Fill = Card });
            stage.Children.Add(shot);
            stage.Children.Add(canvas);
            var frame = new Border
            {
                BorderBrush = Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 10, 0, 0),
                Child = new Viewbox { Stretch = Stretch.Uniform, Child = stage }
            };
            root.Children.Add(frame);
            Content = root;

            canvas.Background = Brushes.Transparent;
            canvas.MouseLeftButtonDown += Canvas_Down;
            canvas.MouseMove += Canvas_Move;
            canvas.MouseLeftButtonUp += (s, e) => { if (dragging != null) { dragging = null; canvas.ReleaseMouseCapture(); Save(); } };
            PreviewKeyDown += OnKey;
            PreviewMouseDown += OnMouseButton;

            LoadImage();
            Refresh();
        }

        static Button MakeButton(string text) { var b = new Button { Content = text }; Look(b); return b; }

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

        void Save()
        {
            cfg.Save();
            changed();
            Refresh();
        }

        // ---------------------------------------------------------------- ภาพอ้างอิง
        void LoadImage()
        {
            try
            {
                if (File.Exists(RefImagePath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;    // ไม่ล็อกไฟล์ไว้
                    bmp.UriSource = new Uri(RefImagePath);
                    bmp.EndInit();
                    SetImage(bmp);
                    return;
                }
            }
            catch (Exception ex) { Log.Error("loot image", ex); }
            // ยังไม่มีภาพ: ใช้ขนาดจอแทน (สัดส่วนเดียวกับเกม)
            W = Math.Max(640, SystemParameters.PrimaryScreenWidth);
            H = Math.Max(360, SystemParameters.PrimaryScreenHeight);
            shot.Source = null;
            stage.Width = W;
            stage.Height = H;
        }

        void SetImage(BitmapSource bmp)
        {
            shot.Source = bmp;
            W = bmp.PixelWidth;
            H = bmp.PixelHeight;
            stage.Width = W;
            stage.Height = H;
        }

        void StoreImage(BitmapSource bmp)
        {
            try
            {
                Directory.CreateDirectory(Settings.KitDir);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var f = File.Create(RefImagePath)) enc.Save(f);
            }
            catch (Exception ex) { Log.Error("save loot image", ex); }
            LoadImage();
            Refresh();
        }

        void PasteImage()
        {
            BitmapSource bmp = null;
            try { if (Clipboard.ContainsImage()) bmp = Clipboard.GetImage(); } catch { }
            if (bmp == null) { hint.Text = "คลิปบอร์ดยังไม่มีภาพ — ในเกมเปิดกระเป๋าแล้วกด PrintScreen ก่อน"; hint.Foreground = Gold; return; }
            StoreImage(new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0));   // ภาพจาก PrintScreen มักมี alpha = 0 (โปร่งใสทั้งภาพ)
        }

        void OpenImage()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "ภาพ|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(dlg.FileName);
                bmp.EndInit();
                StoreImage(bmp);
            }
            catch (Exception ex) { hint.Text = "เปิดภาพไม่ได้: " + ex.Message; hint.Foreground = Gold; }
        }

        // ---------------------------------------------------------------- ลากจุด
        double[] Point(string name) => name switch
        {
            "start" => cfg.LootStart, "step" => cfg.LootStep, "drop" => cfg.LootDrop, _ => cfg.LootCenter
        };

        void Canvas_Down(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(canvas);
            double best = double.MaxValue, grab = W * 0.025;
            dragging = null;
            foreach (var name in new[] { "start", "step", "drop", "center" })
            {
                var q = Point(name);
                double dist = Math.Sqrt(Math.Pow(q[0] * W - p.X, 2) + Math.Pow(q[1] * H - p.Y, 2));
                if (dist < grab && dist < best) { best = dist; dragging = name; }
            }
            if (dragging != null) { canvas.CaptureMouse(); e.Handled = true; }
        }

        void Canvas_Move(object sender, MouseEventArgs e)
        {
            if (dragging == null) return;
            var p = e.GetPosition(canvas);
            var q = Point(dragging);
            q[0] = Math.Round(Math.Clamp(p.X / W, 0, 1), 4);
            q[1] = Math.Round(Math.Clamp(p.Y / H, 0, 1), 4);
            Draw();
        }

        void Refresh()
        {
            power.Content = cfg.LootKey != 0 ? "● เปิดอยู่ — คลิกเพื่อปิด" : "○ ปิดอยู่ — คลิกแล้วกดปุ่มที่จะใช้";
            Look(power);
            power.FontWeight = FontWeights.Bold;
            if (cfg.LootKey != 0) { power.BorderBrush = Accent; power.Foreground = Frozen("#5CE1FF"); }
            Look(keyBtn);
            keyBtn.Content = capturing ? "กดปุ่ม / ปุ่มเมาส์... (Esc = ยกเลิก)" : "ปุ่ม: " + (cfg.LootKey == 0 ? "–" : keyName(cfg.LootKey));
            if (capturing) keyBtn.BorderBrush = Accent;
            for (int i = 0; i < SlotChoices.Length; i++) Mark(slotBtns[i], cfg.LootSlots == SlotChoices[i]);
            for (int i = 0; i < 3; i++) Mark(speedBtns[i], cfg.LootSpeed == i);
            if (!capturing)
            {
                hint.Foreground = Muted;
                hint.Text = shot.Source == null
                    ? "ยังไม่มีภาพ: ในเกมเปิดกระเป๋าแล้วกด PrintScreen แล้วกด \"วางภาพจากคลิปบอร์ด\" (ภาพต้องเป็นทั้งจอ ความละเอียดเดียวกับตอนเล่น)"
                    : "ลากจุดให้ตรงกลางช่องของบนพื้น · เสร็จแล้วปิดหน้าต่างได้เลย ค่าบันทึกเอง";
            }
            Draw();
        }

        static void Mark(Button b, bool sel)
        {
            b.BorderBrush = sel ? Orange : Line;
            b.BorderThickness = new Thickness(sel ? 2 : 1);
            b.Foreground = sel ? Orange : Text;
        }

        void Draw()
        {
            canvas.Children.Clear();
            double r = W * 0.009, font = W * 0.0085, th = Math.Max(1, W * 0.0012);
            double[] a = cfg.LootStart, b = cfg.LootStep, drop = cfg.LootDrop, c = cfg.LootCenter;
            int n = Math.Clamp(cfg.LootSlots, 1, 15);
            for (int i = 0; i < n; i++)
            {
                double x = (a[0] + i * (b[0] - a[0])) * W, y = (a[1] + i * (b[1] - a[1])) * H;
                canvas.Children.Add(new Line
                {
                    X1 = x, Y1 = y, X2 = drop[0] * W, Y2 = drop[1] * H, Stroke = i < 2 ? Green : Orange,
                    StrokeThickness = th, StrokeDashArray = new DoubleCollection { 4, 3 }, Opacity = 0.7, IsHitTestVisible = false
                });
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double x = (a[0] + i * (b[0] - a[0])) * W, y = (a[1] + i * (b[1] - a[1])) * H;
                Dot(x, y, r, Orange, (i + 1).ToString(), font, i == 0 ? "ช่องแรก" : i == 1 ? "ช่องที่ 2 (ระยะห่าง)" : null, i < 2);
            }
            Dot(drop[0] * W, drop[1] * H, r * 1.3, Green, "→", font, "กระเป๋า (ปล่อย)", true);
            Dot(c[0] * W, c[1] * H, r * 0.9, Cyan, "•", font, "กลาง", true);
        }

        void Dot(double x, double y, double r, Brush fill, string text, double font, string label, bool handle)
        {
            var el = new Ellipse
            {
                Width = r * 2, Height = r * 2, Fill = fill, Stroke = handle ? Brushes.White : Brushes.Transparent,
                StrokeThickness = Math.Max(1, r * 0.18), IsHitTestVisible = false
            };
            Canvas.SetLeft(el, x - r);
            Canvas.SetTop(el, y - r);
            canvas.Children.Add(el);
            var t = new TextBlock
            {
                Text = text, FontSize = font, FontWeight = FontWeights.Bold, Foreground = Brushes.Black,
                Width = r * 2, TextAlignment = TextAlignment.Center, IsHitTestVisible = false
            };
            Canvas.SetLeft(t, x - r);
            Canvas.SetTop(t, y - font * 0.68);
            canvas.Children.Add(t);
            if (label == null) return;
            var tag = new Border
            {
                Background = fill, CornerRadius = new CornerRadius(r * 0.3), Padding = new Thickness(r * 0.5, r * 0.15, r * 0.5, r * 0.15),
                Child = new TextBlock { Text = label, FontSize = font, FontWeight = FontWeights.Bold, Foreground = Brushes.Black },
                IsHitTestVisible = false
            };
            Canvas.SetLeft(tag, x + r * 1.4);
            Canvas.SetTop(tag, y - font * 0.8);
            canvas.Children.Add(tag);
        }

        // ---------------------------------------------------------------- ตั้งปุ่ม
        void StartCapture()
        {
            capturing = true;
            hint.Text = "กดปุ่มคีย์บอร์ด หรือปุ่มเมาส์กลาง / Back / Forward ที่จะใช้สั่งเก็บของ · Del = ไม่ใช้ · Esc = ยกเลิก";
            hint.Foreground = Gold;
            Focus();
            Refresh();
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (!capturing) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { capturing = false; Refresh(); return; }
            if (key == Key.Delete || key == Key.Back) { capturing = false; cfg.LootKey = 0; Save(); return; }
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0 || vk > 255) return;
            Pick(vk);
        }

        void OnMouseButton(object sender, MouseButtonEventArgs e)
        {
            if (!capturing) return;
            int vk = e.ChangedButton switch { MouseButton.Middle => 4, MouseButton.XButton1 => 5, MouseButton.XButton2 => 6, _ => 0 };
            if (vk == 0) return;
            e.Handled = true;
            Pick(vk);
        }

        void Pick(int vk)
        {
            // ปุ่มที่ใช้ในเกมอยู่แล้ว (เดิน/ยิง/เก็บของ/เปิดกระเป๋า) ไม่ควรใช้
            if (vk == 0x09 || vk == 0x46 || vk == 0x57 || vk == 0x41 || vk == 0x53 || vk == 0x44 || vk == 0x52 || vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x20)
            { hint.Text = $"{keyName(vk)} ใช้ในเกมอยู่แล้ว — เลือกปุ่มอื่น"; return; }
            if (keyTaken(vk)) { hint.Text = $"{keyName(vk)} ใช้กับอย่างอื่นใน GunBeep อยู่แล้ว — เลือกปุ่มอื่น"; return; }
            capturing = false;
            cfg.LootKey = vk;
            Save();
            hint.Text = $"ตั้งแล้ว: เปิดกระเป๋าในเกมแล้วกด {keyName(vk)}";
            hint.Foreground = Green;
        }
    }
}
