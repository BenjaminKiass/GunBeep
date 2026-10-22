using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GunBeep
{
    // ป้ายเล็กๆ บนมุมจอเกม: เปิด/ปิด + ปืนที่ใช้ + base/x
    // คลิกทะลุ ไม่แย่งโฟกัส ไม่ขึ้น taskbar — ขึ้นได้เฉพาะเกมโหมด Borderless / Windowed
    public class Overlay : Window
    {
        public const int MODE_OFF = 0, MODE_POP = 1, MODE_ALWAYS = 2;
        public static readonly string[] ModeNames = { "ปิด", "ตอนเปลี่ยน", "ตลอด" };
        public static readonly string[] CornerNames = { "↖  ซ้ายบน", "↗  ขวาบน", "↙  ซ้ายล่าง", "↘  ขวาล่าง" };

        static readonly Color ON = Color.FromRgb(0x22, 0xC5, 0x5E);
        static readonly Color OFF = Color.FromRgb(0xEF, 0x44, 0x44);

        readonly Ellipse dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock status = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 0) };
        readonly TextBlock badge = new TextBlock
        {
            FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        readonly Border badgeBox;
        readonly TextBlock name = new TextBlock
        {
            FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            Margin = new Thickness(7, 0, 0, 0), MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis
        };
        readonly TextBlock values = new TextBlock
        {
            FontSize = 11.5, FontFamily = new FontFamily("Consolas, Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x94, 0xBC)), Margin = new Thickness(18, 3, 0, 0)
        };
        readonly TextBlock evt = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 3, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xDB, 0xFF)), Visibility = Visibility.Collapsed
        };
        // ข้อความนำทาง (จูนด้วยตา): ค้างไว้จนกว่าจะเปลี่ยน/ลบ และโชว์แม้ตั้งป้ายเป็น "ปิด"
        readonly TextBlock guide = new TextBlock
        {
            FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 380,
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xD0, 0x6A)),
            Visibility = Visibility.Collapsed
        };
        readonly Border pill;
        readonly DispatcherTimer hideTimer, evtTimer, topTimer;

        int mode = MODE_POP, corner, gen;
        bool shown;

        public Overlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "GunBeep overlay";
            FontFamily = new FontFamily("Segoe UI, Leelawadee UI");

            badgeBox = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(6), Margin = new Thickness(12, 0, 0, 0),
                Background = new LinearGradientBrush(Color.FromRgb(0x1D, 0x6B, 0xFF), Color.FromRgb(0x5C, 0xE1, 0xFF), 45),
                Child = badge
            };
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(dot);
            top.Children.Add(status);
            top.Children.Add(badgeBox);
            top.Children.Add(name);

            var stack = new StackPanel();
            stack.Children.Add(top);
            stack.Children.Add(values);
            stack.Children.Add(evt);
            stack.Children.Add(guide);

            pill = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 14, 8), Margin = new Thickness(10),
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x07, 0x0B, 0x16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x1D, 0x6B, 0xFF)), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.6 },
                Child = stack
            };
            Content = pill;

            hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            hideTimer.Tick += (s, e) => { hideTimer.Stop(); if (mode != MODE_ALWAYS && !Guiding) FadeOut(); };
            evtTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            evtTimer.Tick += (s, e) => { evtTimer.Stop(); evt.Visibility = Visibility.Collapsed; };
            // เกม / หน้าต่างอื่นที่ topmost อาจขึ้นมาทับ → ดันกลับขึ้นบนสุดเรื่อยๆ ระหว่างโชว์
            topTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            topTimer.Tick += (s, e) => BringToTop();

            SizeChanged += (s, e) => Place();
            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
            };
        }

        public void Configure(int newMode, int newCorner)
        {
            mode = Math.Clamp(newMode, MODE_OFF, MODE_ALWAYS);
            corner = Math.Clamp(newCorner, 0, 3);
            Place();
            if (mode == MODE_OFF) HideNow();
            else Pop();   // โชว์ให้เห็นตำแหน่ง/รูปแบบที่เพิ่งตั้ง
        }

        public void SetState(bool on, int gun, string gunName, string vals)
        {
            var c = on ? ON : OFF;
            dot.Fill = new SolidColorBrush(c);
            dot.Effect = new DropShadowEffect { Color = c, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 };
            status.Text = on ? "ON" : "OFF";
            status.Foreground = new SolidColorBrush(c);
            badge.Text = gun.ToString();
            name.Text = gunName;
            values.Text = vals;
            pill.Opacity = on ? 1.0 : 0.75;
        }

        // โชว์ชั่วคราว (โหมด "ตอนเปลี่ยน") พร้อมข้อความเหตุการณ์ถ้ามี
        bool Guiding => guide.Visibility == Visibility.Visible;

        public void Guide(string text)
        {
            gen++;
            if (string.IsNullOrEmpty(text))
            {
                guide.Visibility = Visibility.Collapsed;
                if (mode == MODE_OFF) HideNow();
                else if (mode == MODE_POP) { hideTimer.Stop(); hideTimer.Start(); }
                return;
            }
            guide.Text = text;
            guide.Visibility = Visibility.Visible;
            hideTimer.Stop();
            FadeIn();
        }

        public void Pop(string message = null)
        {
            if (mode == MODE_OFF && !Guiding) return;
            gen++;
            if (!string.IsNullOrEmpty(message))
            {
                evt.Text = message;
                evt.Visibility = Visibility.Visible;
                evtTimer.Stop();
                evtTimer.Start();
            }
            FadeIn();
            hideTimer.Stop();
            if (mode == MODE_POP && !Guiding) hideTimer.Start();
        }

        void FadeIn()
        {
            if (!shown)
            {
                shown = true;
                Opacity = 0;
                Show();
                topTimer.Start();
            }
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            BringToTop();
        }

        void FadeOut()
        {
            int g = gen;
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
            a.Completed += (s, e) => { if (g == gen && mode != MODE_ALWAYS && !Guiding) HideNow(); };
            BeginAnimation(OpacityProperty, a);
        }

        void HideNow()
        {
            hideTimer.Stop();
            topTimer.Stop();
            BeginAnimation(OpacityProperty, null);
            if (shown) { shown = false; Hide(); }
        }

        void Place()
        {
            // มุมของจอหลัก (เกมเต็มจอ จึงใช้ขนาดจอทั้งหมด ไม่ใช่พื้นที่เหนือ taskbar)
            double w = ActualWidth > 0 ? ActualWidth : 260, h = ActualHeight > 0 ? ActualHeight : 80;
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            const double m = 8;
            Left = corner == 0 || corner == 2 ? m : sw - w - m;
            Top = corner < 2 ? m : sh - h - m;
        }

        void BringToTop()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero)
                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    }
}
