using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using D = System.Drawing;

namespace GunBeep
{
    // ตั้งค่าตรวจสโคป: ลากจุดให้ตรงกล่องเลขช่องปืน + ช่องสโคปบนภาพหน้ากระเป๋า แล้วสอนว่าสโคปไหนใช้ชุดค่าไหน
    public sealed class ScopeWindow : Window
    {
        static readonly Brush Bg = Frozen("#0E1628"), Card = Frozen("#16213A"), Line = Frozen("#2A3A5C"),
                              Text = Frozen("#E6EDF8"), Muted = Frozen("#8A9BB8"), Accent = Frozen("#1D6BFF"), Gold = Frozen("#C9A54A"),
                              Orange = Frozen("#F2A516"), Green = Frozen("#19C37D"), Cyan = Frozen("#22C3EE");
        static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        // ส่วนของภาพที่โชว์ (แผงปืนอยู่ขวาบน) — สัดส่วนของจอ
        const double VX = 0.50, VY = 0.0, VW = 0.50, VH = 0.62;

        public static string RefImagePath => System.IO.Path.Combine(Settings.KitDir, "scope-ref.png");

        readonly Settings cfg;
        readonly Action changed;
        readonly Func<int, string> keyName;
        readonly Grid stage = new();
        readonly Image shot = new() { Stretch = Stretch.Fill };
        readonly Canvas canvas = new() { Background = Brushes.Transparent };
        readonly TextBlock hint = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        readonly Button power = new(), key1 = new(), key2 = new();
        readonly Button[] noneBtns = new Button[4];
        readonly StackPanel results = new(), gallery = new();
        D.Bitmap refBmp;
        int W = 1920, H = 1080;
        string dragging;
        int capturing;                                   // 1/2 = กำลังรอปุ่มปืนช่อง 1/2

        public bool Capturing => capturing != 0;

        public ScopeWindow(Settings cfg, Func<int, string> keyName, Action changed)
        {
            this.cfg = cfg;
            this.keyName = keyName;
            this.changed = changed;
            Title = "ตรวจสโคปอัตโนมัติ — ตั้งค่า";
            Width = 1180;
            Height = 800;
            MinWidth = 900;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Bg;
            FontFamily = new FontFamily("Segoe UI, Leelawadee UI");
            FontSize = 13;
            Foreground = Text;

            var root = new DockPanel { Margin = new Thickness(18) };
            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = "ตรวจสโคปอัตโนมัติ", FontSize = 16, FontWeight = FontWeights.Bold });
            head.Children.Add(new TextBlock
            {
                Text = "เปิดกระเป๋า (Tab) แล้ว GunBeep ดูแผงปืนทางขวา: เลขช่อง · ชื่อปืน · สโคปที่ติด → กดเปลี่ยนปืนในเกมแล้วใช้ชุดค่าตามสโคปของปืนนั้นเอง\n" +
                       "ตั้งครั้งแรก: ในเกมเปิดกระเป๋า กด PrintScreen → วางภาพ → ลากจุด ① ② ให้ตรงกล่องเลขช่องปืน และจุด S ให้ตรงช่องสโคปแผงบน → กดปุ่มสอนว่าสโคปนี้ใช้ชุดไหน (ทำกับทุกแบบสโคปที่ใช้)",
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
            });

            var row1 = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            power.Click += (s, e) => { cfg.ScopeAuto = !cfg.ScopeAuto; Save(); };
            row1.Children.Add(power);
            row1.Children.Add(Btn("วางภาพจากคลิปบอร์ด", PasteImage, 18));
            row1.Children.Add(Btn("เลือกไฟล์ภาพ...", OpenImage, 6));
            row1.Children.Add(Btn("ตำแหน่งเริ่มต้น", () =>
            {
                var d = new Settings();
                cfg.ScopeBadge1 = d.ScopeBadge1; cfg.ScopeBadge2 = d.ScopeBadge2; cfg.ScopeIcon1 = d.ScopeIcon1; cfg.ScopeCalib = null;   // ให้หาตำแหน่งเองใหม่
                Save(); Analyze();
            }, 6));
            head.Children.Add(row1);

            var row2 = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            row2.Children.Add(Lbl("ปืนไม่มีสโคป ใช้ชุด"));
            for (int i = 0; i < 4; i++)
            {
                int v = i;
                noneBtns[i] = Btn(i == 0 ? "ไม่เปลี่ยน" : $"{i} · {cfg.Names[i - 1]}", () => { cfg.ScopeNoneSet = v; Save(); }, i == 0 ? 8 : 4);
                row2.Children.Add(noneBtns[i]);
            }
            row2.Children.Add(Lbl("ปุ่มในเกม  ปืนช่อง 1", 22));
            key1.Click += (s, e) => StartCapture(1);
            key1.Margin = new Thickness(6, 0, 0, 0);
            row2.Children.Add(key1);
            row2.Children.Add(Lbl("ช่อง 2", 10));
            key2.Click += (s, e) => StartCapture(2);
            key2.Margin = new Thickness(6, 0, 0, 0);
            row2.Children.Add(key2);
            row2.Children.Add(Btn("อ่านจาก PUBG", () =>
            {
                var k = ScopeDetect.ReadPubgSlotKeys();
                if (k == null) { Hint("อ่านปุ่มจากไฟล์ตั้งค่า PUBG ไม่ได้ — ตั้งเองโดยคลิกช่องปุ่ม", Gold); return; }
                cfg.SlotKeys = null; Save();                     // null = อ่านจาก PUBG ทุกครั้งที่เปิดเกม
                Hint($"อ่านจาก PUBG: ปืนช่อง 1 = {keyName(k[0])}, ช่อง 2 = {keyName(k[1])}", Green);
            }, 6));
            head.Children.Add(row2);
            head.Children.Add(hint);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            // ขวา: ผลที่อ่านได้ + ปุ่มสอน + ตัวอย่างที่สอนแล้ว
            var right = new StackPanel { Width = 330, Margin = new Thickness(14, 10, 0, 0) };
            right.Children.Add(Header("อ่านจากภาพนี้"));
            right.Children.Add(results);
            right.Children.Add(Header("สโคปที่สอนแล้ว"));
            right.Children.Add(new ScrollViewer { Content = gallery, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            DockPanel.SetDock(right, Dock.Right);
            root.Children.Add(right);

            stage.Children.Add(new Rectangle { Fill = Card });
            stage.Children.Add(shot);
            stage.Children.Add(canvas);
            root.Children.Add(new Border
            {
                BorderBrush = Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 10, 0, 0),
                Child = new Viewbox { Stretch = Stretch.Uniform, Child = stage }
            });
            Content = root;

            canvas.MouseLeftButtonDown += Canvas_Down;
            canvas.MouseMove += Canvas_Move;
            canvas.MouseLeftButtonUp += (s, e) => { if (dragging != null) { dragging = null; canvas.ReleaseMouseCapture(); cfg.ScopeCalib = $"{W}x{H}"; Save(); Analyze(); } };
            PreviewKeyDown += OnKey;
            Closed += (s, e) => refBmp?.Dispose();

            LoadImage();
            Refresh();
            Analyze();
        }

        // ---------------------------------------------------------------- ส่วนประกอบ
        Button Btn(string text, Action click, double left)
        {
            var b = new Button { Content = text, Margin = new Thickness(left, 0, 0, 0) };
            Look(b);
            b.Click += (s, e) => click();
            return b;
        }

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

        static TextBlock Lbl(string t, double left = 0) =>
            new() { Text = t, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 0, 0) };

        static TextBlock Header(string t) => new() { Text = t, Foreground = Muted, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };

        void Hint(string text, Brush color) { hint.Text = text; hint.Foreground = color; }

        void Save()
        {
            cfg.Save();
            changed();
            Refresh();
        }

        void Refresh()
        {
            power.Content = cfg.ScopeAuto ? "● เปิดอยู่ — คลิกเพื่อปิด" : "○ ปิดอยู่ — คลิกเพื่อเปิด";
            Look(power);
            power.FontWeight = FontWeights.Bold;
            if (cfg.ScopeAuto) { power.BorderBrush = Accent; power.Foreground = Frozen("#5CE1FF"); }
            for (int i = 0; i < 4; i++)
            {
                bool sel = cfg.ScopeNoneSet == i;
                noneBtns[i].BorderBrush = sel ? Orange : Line;
                noneBtns[i].Foreground = sel ? Orange : Text;
            }
            Look(key1); Look(key2);
            var keys = cfg.SlotKeys ?? ScopeDetect.ReadPubgSlotKeys() ?? new[] { 0x31, 0x32 };
            key1.Content = capturing == 1 ? "กดปุ่ม..." : keyName(keys[0]);
            key2.Content = capturing == 2 ? "กดปุ่ม..." : keyName(keys[1]);
            if (capturing == 1) key1.BorderBrush = Accent;
            if (capturing == 2) key2.BorderBrush = Accent;
            if (hint.Text == "") Hint(refBmp == null
                ? "ยังไม่มีภาพ: ในเกมเปิดกระเป๋า (มีปืนติดสโคป) กด PrintScreen แล้วกด \"วางภาพจากคลิปบอร์ด\""
                : "ลากจุดให้ตรงแล้วกดปุ่มสอนทางขวา · สอนสโคปละ 2–3 ภาพ (คนละปืน) จะแม่นขึ้น", Muted);
            Draw();
            Gallery();
        }

        // ---------------------------------------------------------------- ภาพ
        void LoadImage()
        {
            refBmp?.Dispose();
            refBmp = null;
            try
            {
                if (File.Exists(RefImagePath))
                {
                    using (var tmp = new D.Bitmap(RefImagePath)) refBmp = new D.Bitmap(tmp);   // ไม่ล็อกไฟล์
                    W = refBmp.Width;
                    H = refBmp.Height;
                    shot.Source = ToSource(refBmp, Rect());
                }
            }
            catch (Exception ex) { Log.Error("scope image", ex); }
            if (refBmp == null)
            {
                W = (int)SystemParameters.PrimaryScreenWidth;
                H = (int)SystemParameters.PrimaryScreenHeight;
                shot.Source = null;
            }
            var r = Rect();
            stage.Width = r.Width;
            stage.Height = r.Height;
        }

        D.Rectangle Rect() => new((int)(VX * W), (int)(VY * H), (int)(VW * W), (int)(VH * H));

        static BitmapSource ToSource(D.Bitmap bmp, D.Rectangle? part = null)
        {
            using var ms = new MemoryStream();
            if (part is D.Rectangle r) using (var c = ScopeDetect.Crop(bmp, r)) c.Save(ms, D.Imaging.ImageFormat.Png);
            else bmp.Save(ms, D.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.StreamSource = ms;
            b.EndInit();
            b.Freeze();
            return b;
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
            catch (Exception ex) { Log.Error("save scope image", ex); }
            LoadImage();
            hint.Text = "";
            Refresh();
            Analyze();
        }

        void PasteImage()
        {
            BitmapSource bmp = null;
            try { if (Clipboard.ContainsImage()) bmp = Clipboard.GetImage(); } catch { }
            if (bmp == null) { Hint("คลิปบอร์ดยังไม่มีภาพ — ในเกมเปิดกระเป๋าแล้วกด PrintScreen ก่อน", Gold); return; }
            StoreImage(new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0));
        }

        void OpenImage()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "ภาพ|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(dlg.FileName);
                bmp.EndInit();
                StoreImage(new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0));
            }
            catch (Exception ex) { Hint("เปิดภาพไม่ได้: " + ex.Message, Gold); }
        }

        // ---------------------------------------------------------------- จุดบนภาพ (พิกัดในภาพที่โชว์)
        double SX(double fx) => (fx - VX) * W;
        double SY(double fy) => (fy - VY) * H;

        double[] Point(string n) => n == "b1" ? cfg.ScopeBadge1 : n == "b2" ? cfg.ScopeBadge2 : cfg.ScopeIcon1;

        void Canvas_Down(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(canvas);
            double best = double.MaxValue, grab = H * 0.03;
            dragging = null;
            foreach (var n in new[] { "b1", "b2", "icon" })
            {
                var q = Point(n);
                double d = Math.Sqrt(Math.Pow(SX(q[0]) - p.X, 2) + Math.Pow(SY(q[1]) - p.Y, 2));
                if (d < grab && d < best) { best = d; dragging = n; }
            }
            if (dragging != null) { canvas.CaptureMouse(); e.Handled = true; }
        }

        void Canvas_Move(object sender, MouseEventArgs e)
        {
            if (dragging == null) return;
            var p = e.GetPosition(canvas);
            var q = Point(dragging);
            q[0] = Math.Round(Math.Clamp(p.X / W + VX, 0, 1), 4);
            q[1] = Math.Round(Math.Clamp(p.Y / H + VY, 0, 1), 4);
            Draw();
        }

        void Draw()
        {
            canvas.Children.Clear();
            var boxes = ScopeDetect.Boxes(cfg, W, H);
            double th = Math.Max(1, H * 0.002);
            for (int i = 0; i < 2; i++)
            {
                Box(boxes[i].badge, Orange, th);
                Box(boxes[i].name, Muted, th);
                Box(boxes[i].icon, i == 0 ? Green : Cyan, th);
            }
            double r = H * 0.009;
            Dot(cfg.ScopeBadge1, Orange, "1", r, "เลขช่องแผงบน");
            Dot(cfg.ScopeBadge2, Orange, "2", r, "เลขช่องแผงล่าง");
            Dot(cfg.ScopeIcon1, Green, "S", r, "สโคปแผงบน (แผงล่างตามระยะเดียวกัน)");
        }

        void Box(D.Rectangle b, Brush c, double th)
        {
            var rect = new Rectangle { Width = b.Width, Height = b.Height, Stroke = c, StrokeThickness = th, IsHitTestVisible = false };
            Canvas.SetLeft(rect, b.X - VX * W);
            Canvas.SetTop(rect, b.Y - VY * H);
            canvas.Children.Add(rect);
        }

        void Dot(double[] f, Brush fill, string text, double r, string label)
        {
            double x = SX(f[0]), y = SY(f[1]);
            var el = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = Brushes.White, StrokeThickness = r * 0.2, Opacity = 0.9, IsHitTestVisible = false };
            Canvas.SetLeft(el, x - r);
            Canvas.SetTop(el, y - r);
            canvas.Children.Add(el);
            var t = new TextBlock { Text = text, FontSize = r * 1.3, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, Width = r * 2, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
            Canvas.SetLeft(t, x - r);
            Canvas.SetTop(t, y - r * 0.85);
            canvas.Children.Add(t);
            var tag = new Border
            {
                Background = fill, CornerRadius = new CornerRadius(r * 0.3), Padding = new Thickness(r * 0.4, 0, r * 0.4, 0), IsHitTestVisible = false,
                Child = new TextBlock { Text = label, FontSize = r * 1.1, Foreground = Brushes.Black }
            };
            Canvas.SetLeft(tag, x - r * 12);
            Canvas.SetTop(tag, y + r * 1.4);
            canvas.Children.Add(tag);
        }

        // ---------------------------------------------------------------- อ่านภาพ + สอน
        async void Analyze()
        {
            results.Children.Clear();
            if (refBmp == null) { results.Children.Add(Lbl("— ยังไม่มีภาพ —")); return; }
            var panels = ScopeDetect.Read(cfg, refBmp, D.Point.Empty, W, H);
            var boxes = ScopeDetect.Boxes(cfg, W, H);
            var ocrJobs = new System.Collections.Generic.List<(TextBlock info, D.Bitmap crop, string pre, string post)>();
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                var p = panels[i];
                var card = new Border { Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) };
                var stack = new StackPanel();
                var top = new DockPanel();
                using (var ic = ScopeDetect.Crop(refBmp, boxes[i].icon))
                    top.Children.Add(new Image { Source = ToSource(ic), Width = 56, Height = 56, Margin = new Thickness(0, 0, 8, 0) });
                var info = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                string slot = p.Slot == 0 ? "ไม่มีปืน / อ่านเลขไม่ออก" : $"ปืนช่อง {p.Slot}";
                string scope = p.Set < 0 ? $"สโคป: ยังไม่รู้จัก ({p.Score:0.00})" : p.Set == 0 ? "สโคป: ไม่มี" : $"สโคป: {p.ScopeLabel} → ชุด {p.Set} ({p.Score:0.00})";
                info.Text = $"{(i == 0 ? "แผงบน" : "แผงล่าง")} · {slot}\n{scope}";
                top.Children.Add(info);
                stack.Children.Add(top);
                var teach = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                teach.Children.Add(Lbl("สอนว่าใช้:"));
                for (int s = 1; s <= 3; s++)
                {
                    int set = s;
                    var b = Btn($"{s} · {cfg.Names[s - 1]}", () => Teach(idx, set), 4);
                    b.Padding = new Thickness(7, 3, 7, 3);
                    teach.Children.Add(b);
                }
                card.Child = stack;
                stack.Children.Add(teach);
                results.Children.Add(card);
                if (p.Slot != 0)
                    ocrJobs.Add((info, ScopeDetect.Crop(refBmp, boxes[i].name), $"{(i == 0 ? "แผงบน" : "แผงล่าง")} · {slot} · ", "\n" + scope));
            }
            // ชื่อปืน (OCR) ทีหลัง — งาน OCR อาจกลับมาคนละเธรด จึงแก้หน้าจอผ่าน Dispatcher
            foreach (var (info, crop, pre, post) in ocrJobs)
            {
                string name;
                using (crop) name = await ScopeDetect.ReadName(crop);
                if (name != "") info.Dispatcher.Invoke(() => info.Text = pre + name + post);
            }
        }

        void Teach(int panel, int set)
        {
            if (refBmp == null) return;
            var box = ScopeDetect.Boxes(cfg, W, H)[panel].icon;
            using var ic = ScopeDetect.Crop(refBmp, box);
            if (ScopeDetect.Detail(ic) < 0.035) { Hint("ช่องสโคปนี้ว่าง (ไม่มีสโคป) — ไม่ต้องสอน ตั้ง \"ปืนไม่มีสโคป ใช้ชุด\" แทน", Gold); return; }
            ScopeDetect.Teach(ic, set, cfg.Names[set - 1]);
            Hint($"จำแล้ว: สโคปนี้ = ชุด {set} ({cfg.Names[set - 1]}) · ตอนนี้มีตัวอย่าง {ScopeDetect.Templates.Count} ภาพ", Green);
            Gallery();
            Analyze();
        }

        void Gallery()
        {
            gallery.Children.Clear();
            var list = ScopeDetect.Templates.OrderBy(t => t.Set).ToList();
            if (list.Count == 0) { gallery.Children.Add(Lbl("ยังไม่ได้สอน")); return; }
            var wrap = new WrapPanel();
            foreach (var t in list)
            {
                var cell = new StackPanel { Width = 72, Margin = new Thickness(0, 0, 6, 6) };
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = new Uri(ScopeDetect.ImagePath(t));
                    img.EndInit();
                    cell.Children.Add(new Image { Source = img, Width = 56, Height = 56 });
                }
                catch { cell.Children.Add(new Rectangle { Width = 56, Height = 56, Fill = Card }); }
                cell.Children.Add(new TextBlock { Text = $"ชุด {t.Set}", Foreground = Text, FontSize = 11, TextAlignment = TextAlignment.Center });
                var del = Btn("ลบ", () => { ScopeDetect.Forget(t); Gallery(); Analyze(); }, 0);
                del.Padding = new Thickness(4, 1, 4, 1);
                del.FontSize = 11;
                del.HorizontalAlignment = HorizontalAlignment.Center;
                cell.Children.Add(del);
                wrap.Children.Add(cell);
            }
            gallery.Children.Add(wrap);
        }

        // ---------------------------------------------------------------- ตั้งปุ่มปืนช่อง 1/2
        void StartCapture(int which)
        {
            capturing = which;
            Hint($"กดปุ่มที่ใช้เลือกปืนช่อง {which} ในเกม · Esc = ยกเลิก", Gold);
            Focus();
            Refresh();
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (capturing == 0) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int which = capturing;
            capturing = 0;
            if (key != Key.Escape)
            {
                int vk = KeyInterop.VirtualKeyFromKey(key);
                if (vk > 0 && vk < 256)
                {
                    var k = (cfg.SlotKeys ?? ScopeDetect.ReadPubgSlotKeys() ?? new[] { 0x31, 0x32 }).ToArray();
                    k[which - 1] = vk;
                    cfg.SlotKeys = k;
                    cfg.Save();
                    changed();
                    Hint($"ปืนช่อง {which} = {keyName(vk)}", Green);
                }
            }
            Refresh();
        }
    }
}
