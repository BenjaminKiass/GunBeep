using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GunBeep
{
    /// <summary>
    /// ล็อกขนาดหน้าต่างหลัก (ผู้ใช้ขอ 2026-11-13): ขนาดพอดีกับจอแต่ละขนาด (ย่อ/ขยายทั้งหน้า UI ตามสัดส่วน),
    /// ลากขอบ / Aero Snap / ลากไปชนขอบบน / Win+↑ ปรับขนาดไม่ได้. ขยายเต็มจอได้ทางเดียว = ปุ่มสี่เหลี่ยม (AllowMaximize),
    /// กดอีกทีกลับมาขนาดล็อก.
    /// </summary>
    sealed class WindowFit
    {
        public const double BaseW = 1210, BaseH = 800;      // ขนาดที่ออกแบบหน้า UI ไว้ (สเกล 1.0)

        readonly Window w;
        readonly FrameworkElement root;
        bool allowMax;
        public double Scale { get; private set; } = 1;

        public WindowFit(Window window, FrameworkElement content)
        {
            w = window; root = content;
            w.SourceInitialized += (s, e) => HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)?.AddHook(Hook);
            w.StateChanged += (s, e) =>
            {
                // ขยายเต็มจอด้วยวิธีอื่นที่ไม่ใช่ปุ่ม (ลากไปขอบบน, Win+↑, ดับเบิลคลิก) → กลับขนาดล็อก
                if (w.WindowState == WindowState.Maximized && !allowMax) w.WindowState = WindowState.Normal;
                if (w.WindowState == WindowState.Normal) { allowMax = false; FitNormal(); }
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => w.Dispatcher.BeginInvoke(new Action(Apply));
            Apply();
        }

        /// <summary>สเกลจากพื้นที่จอ (ไม่รวมแถบงาน): 1080p = 1.0, จอเล็กย่อลง, จอ 2K/4K ขยายขึ้น. ปัดลงทีละ 0.05.</summary>
        public static double FitScale(Rect work)
        {
            double s = Math.Min(work.Width * 0.66 / BaseW, work.Height * 0.80 / BaseH);
            s = Math.Floor(s * 20 + 1e-6) / 20;
            return Math.Clamp(s, 0.6, 1.6);
        }

        public void Apply()
        {
            Scale = FitScale(SystemParameters.WorkArea);
            root.LayoutTransform = Math.Abs(Scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(Scale, Scale);
            if (w.WindowState == WindowState.Normal) FitNormal();
        }

        public Size NormalSize => new Size(Math.Round(BaseW * Scale), Math.Round(BaseH * Scale));

        void FitNormal()
        {
            var sz = NormalSize;
            w.Width = sz.Width; w.Height = sz.Height;
            var wa = SystemParameters.WorkArea;
            if (!double.IsNaN(w.Left) && !double.IsNaN(w.Top))
            {
                // ให้อยู่ในจอเสมอ (เช่นเปลี่ยนจอ/ความละเอียดแล้วหน้าต่างหลุดขอบ)
                w.Left = Math.Max(wa.Left, Math.Min(w.Left, wa.Right - sz.Width));
                w.Top = Math.Max(wa.Top, Math.Min(w.Top, wa.Bottom - sz.Height));
            }
        }

        /// <summary>ปุ่มสี่เหลี่ยม: เต็มจอ ↔ ขนาดล็อก.</summary>
        public void ToggleMaximize()
        {
            if (w.WindowState == WindowState.Maximized) w.WindowState = WindowState.Normal;
            else { allowMax = true; w.WindowState = WindowState.Maximized; }
        }

        // ---- กันปรับขนาดในสถานะปกติ (Aero Snap ครึ่งจอ, โปรแกรมอื่นสั่ง, ลากขอบ)
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        const int WM_WINDOWPOSCHANGING = 0x0046;
        const uint SWP_NOSIZE = 0x0001;

        // ---- เต็มจอ = พื้นที่ทำงานของจอ (ไม่ทับแถบงาน). หน้าต่างไม่มีกรอบ (WindowStyle None) ปกติ Windows ขยายทับแถบงาน
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT reserved, maxSize, maxPosition, minTrack, maxTrack; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT monitor, work; public uint flags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        const int WM_GETMINMAXINFO = 0x0024;

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                var mon = MonitorFromWindow(hwnd, 2);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                {
                    var mm = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mm.maxPosition.x = mi.work.l - mi.monitor.l;
                    mm.maxPosition.y = mi.work.t - mi.monitor.t;
                    mm.maxSize.x = mi.work.r - mi.work.l;
                    mm.maxSize.y = mi.work.b - mi.work.t;
                    Marshal.StructureToPtr(mm, lParam, false);
                }
            }
            if (msg == WM_WINDOWPOSCHANGING && w.WindowState == WindowState.Normal && !IsZoomed(hwnd) && !IsIconic(hwnd))
            {
                var p = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((p.flags & SWP_NOSIZE) == 0)
                {
                    var dpi = VisualTreeHelper.GetDpi(w);
                    var sz = NormalSize;
                    int cx = (int)Math.Round(sz.Width * dpi.DpiScaleX), cy = (int)Math.Round(sz.Height * dpi.DpiScaleY);
                    if (p.cx != cx || p.cy != cy) { p.cx = cx; p.cy = cy; Marshal.StructureToPtr(p, lParam, false); }
                }
            }
            return IntPtr.Zero;
        }
    }
}
