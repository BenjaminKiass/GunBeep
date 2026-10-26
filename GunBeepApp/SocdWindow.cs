using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GunBeep
{
    // หน้าตั้งค่า SOCD: เปิด/ปิด, เลือกโหมด, เพิ่ม/ลบคู่ปุ่ม (กดปุ่มบนคีย์บอร์ดเพื่อเลือก)
    public sealed class SocdWindow : Window
    {
        static readonly string[] ModeNames = { "ปุ่มล่าสุดชนะ", "ปุ่มแรกชนะ", "กดคู่ = หยุดนิ่ง" };
        static readonly string[] ModeTips =
        {
            "กด A ค้างแล้วกด D = ไปขวาทันที ปล่อย D = กลับไปซ้ายเอง (แบบ Snap Tap) — เหมาะกับส่ายหลบกระสุน",
            "กด A ค้างแล้วกด D = ยังไปซ้าย จนกว่าจะปล่อย A",
            "กด A กับ D พร้อมกัน = ตัวละครหยุดนิ่ง (แบบจอยเกมต่อสู้)",
        };

        static readonly Brush Bg = Frozen("#0E1628"), Card = Frozen("#16213A"), Line = Frozen("#2A3A5C"),
                              Text = Frozen("#E6EDF8"), Muted = Frozen("#8A9BB8"), Accent = Frozen("#1D6BFF"), Gold = Frozen("#C9A54A");

        static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        readonly Settings cfg;
        readonly Action changed;
        readonly Func<int, string> keyName;
        readonly StackPanel list = new();
        readonly TextBlock status = new() { Foreground = Gold, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        readonly Button power = new(), add = new();
        readonly Button[] modes = new Button[3];
        int captureStep, firstKey;

        public SocdWindow(Settings cfg, Func<int, string> keyName, Action changed)
        {
            this.cfg = cfg;
            this.keyName = keyName;
            this.changed = changed;
            Title = "SOCD";
            Width = 400;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Bg;
            FontFamily = new FontFamily("Segoe UI, Leelawadee UI");
            FontSize = 13;
            Foreground = Text;

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock { Text = "SOCD — กดปุ่มทิศตรงข้ามพร้อมกัน", FontSize = 16, FontWeight = FontWeights.Bold });
            root.Children.Add(new TextBlock
            {
                Text = "เหมือนคีย์บอร์ดแบรนด์แพง (Snap Tap) แต่ใช้ได้กับคีย์บอร์ดทุกตัว เลือกได้ว่าคู่ปุ่มไหน และให้ปุ่มไหนชนะ",
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12)
            });

            power.Click += (s, e) => { cfg.Socd = !cfg.Socd; Save(); };
            root.Children.Add(power);

            root.Children.Add(Header("โหมด"));
            var mp = new UniformGrid3();
            for (int i = 0; i < 3; i++)
            {
                int m = i;
                modes[i] = MakeButton(ModeNames[i]);
                modes[i].ToolTip = ModeTips[i];
                modes[i].Margin = new Thickness(i == 0 ? 0 : 3, 0, i == 2 ? 0 : 3, 0);
                modes[i].Click += (s, e) => { cfg.SocdMode = m; Save(); };
                mp.Add(modes[i]);
            }
            root.Children.Add(mp.G);

            var gameOnly = new CheckBox
            {
                Content = "ใช้เฉพาะตอนอยู่ในเกม PUBG (พิมพ์งานปกติไม่โดน)", IsChecked = cfg.SocdGameOnly,
                Foreground = Text, Margin = new Thickness(0, 12, 0, 0)
            };
            gameOnly.Click += (s, e) => { cfg.SocdGameOnly = gameOnly.IsChecked == true; Save(); };
            root.Children.Add(gameOnly);

            root.Children.Add(Header("คู่ปุ่ม"));
            root.Children.Add(list);
            add.Content = "＋ เพิ่มคู่ปุ่ม";
            Look(add);
            add.Margin = new Thickness(0, 6, 0, 0);
            add.Click += (s, e) => StartCapture();
            root.Children.Add(add);
            root.Children.Add(status);

            Content = root;
            PreviewKeyDown += OnKey;
            Refresh();
        }

        static TextBlock Header(string t) => new() { Text = t, Foreground = Muted, Margin = new Thickness(0, 14, 0, 6) };

        static Button MakeButton(string text) { var b = new Button { Content = text }; Look(b); return b; }

        static void Look(Button b)
        {
            b.Background = Card;
            b.Foreground = Text;
            b.BorderBrush = Line;
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(10, 7, 10, 7);
            b.Cursor = Cursors.Hand;
            b.Focusable = false;
        }

        void Save()
        {
            cfg.Save();
            changed();
            Refresh();
        }

        void Refresh()
        {
            power.Content = cfg.Socd ? "● เปิดอยู่ — คลิกเพื่อปิด" : "○ ปิดอยู่ — คลิกเพื่อเปิด";
            Look(power);
            power.FontWeight = FontWeights.Bold;
            if (cfg.Socd) { power.BorderBrush = Accent; power.Foreground = Frozen("#5CE1FF"); }
            for (int i = 0; i < 3; i++)
            {
                modes[i].BorderBrush = cfg.SocdMode == i ? Accent : Line;
                modes[i].BorderThickness = new Thickness(cfg.SocdMode == i ? 2 : 1);
            }

            list.Children.Clear();
            var pairs = cfg.SocdPairs ?? new List<int[]>();
            if (pairs.Count == 0)
                list.Children.Add(new TextBlock { Text = "ยังไม่มีคู่ปุ่ม", Foreground = Muted });
            foreach (var p in pairs.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var del = MakeButton("ลบ");
                del.Padding = new Thickness(10, 3, 10, 3);
                del.Click += (s, e) => { cfg.SocdPairs.Remove(p); Save(); };
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(del);
                row.Children.Add(new TextBlock
                {
                    Text = $"{keyName(p[0])}  ⇄  {keyName(p[1])}", FontSize = 15, FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                list.Children.Add(row);
            }
        }

        void StartCapture()
        {
            captureStep = 1;
            status.Text = "กดปุ่มแรกบนคีย์บอร์ด (เช่น A) … Esc = ยกเลิก";
            Focus();
        }

        bool Used(int vk) => cfg.SocdPairs?.Any(p => p[0] == vk || p[1] == vk) == true;

        void OnKey(object sender, KeyEventArgs e)
        {
            if (captureStep == 0) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (key == Key.Escape) { captureStep = 0; status.Text = ""; return; }
            if (vk <= 0 || vk > 255) return;
            if (Used(vk)) { status.Text = $"{keyName(vk)} อยู่ในคู่อื่นแล้ว — เลือกปุ่มอื่น"; return; }
            if (captureStep == 1)
            {
                firstKey = vk;
                captureStep = 2;
                status.Text = $"{keyName(vk)} ⇄ … กดปุ่มที่สอง (เช่น D)";
                return;
            }
            if (vk == firstKey) { status.Text = "ต้องเป็นคนละปุ่มกัน — กดปุ่มที่สอง"; return; }
            captureStep = 0;
            cfg.SocdPairs ??= new List<int[]>();
            cfg.SocdPairs.Add(new[] { firstKey, vk });
            status.Text = $"เพิ่ม {keyName(firstKey)} ⇄ {keyName(vk)} แล้ว";
            Save();
        }

        // แถวปุ่มกว้างเท่ากัน 3 ช่อง
        sealed class UniformGrid3
        {
            public readonly Grid G = new();
            public UniformGrid3() { for (int i = 0; i < 3; i++) G.ColumnDefinitions.Add(new ColumnDefinition()); }
            public void Add(UIElement e) { Grid.SetColumn(e, G.Children.Count); G.Children.Add(e); }
        }
    }
}
