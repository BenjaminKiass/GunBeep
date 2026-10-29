using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace GunBeep
{
    // เปลี่ยนความละเอียดจอหลักตอนเปิดเกม/ตลอด (จอยืดแบบ Borderless) แล้วคืนค่าเดิมตอนปิดเกม/ปิด GunBeep
    //   ใช้ CDS_FULLSCREEN = เปลี่ยนชั่วคราว ไม่เขียนลง registry → รีสตาร์ทเครื่องแล้วกลับเป็นค่าเดิมเสมอ
    public static class Display
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        const int ENUM_CURRENT_SETTINGS = -1, ENUM_REGISTRY_SETTINGS = -2, CDS_FULLSCREEN = 4, DISP_CHANGE_SUCCESSFUL = 0;
        const int DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ChangeDisplaySettingsEx(string device, IntPtr dm, IntPtr hwnd, int flags, IntPtr param);

        static DEVMODE New() => new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

        public static (int w, int h, int hz) Current()
        {
            var dm = New();
            return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) ? (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : (0, 0, 0);
        }

        // ความละเอียดที่ตั้งไว้ใน Windows (ค่าที่ Restore จะกลับไป) — ไม่เปลี่ยนตามที่ GunBeep สลับชั่วคราว
        public static (int w, int h) Desktop()
        {
            var dm = New();
            return EnumDisplaySettings(null, ENUM_REGISTRY_SETTINGS, ref dm) ? (dm.dmPelsWidth, dm.dmPelsHeight) : (0, 0);
        }

        static List<DEVMODE> Modes()
        {
            var list = new List<DEVMODE>();
            for (int i = 0; i < 2000; i++)
            {
                var dm = New();
                if (!EnumDisplaySettings(null, i, ref dm)) break;
                if (dm.dmBitsPerPel == 32) list.Add(dm);
            }
            return list;
        }

        // ความละเอียดที่การ์ดจอรองรับ (ใหญ่ไปเล็ก)
        public static List<(int w, int h)> Resolutions() =>
            Modes().Select(m => (m.dmPelsWidth, m.dmPelsHeight)).Distinct()
                   .OrderByDescending(r => r.Item1 * r.Item2).ThenByDescending(r => r.Item1).ToList();

        public static bool Has(int w, int h) => Modes().Any(m => m.dmPelsWidth == w && m.dmPelsHeight == h);

        public enum Result { Ok, Missing, Failed, Already }

        public static Result Switch(int w, int h)
        {
            var cur = Current();
            if (cur.w == w && cur.h == h) return Result.Already;
            var modes = Modes().Where(m => m.dmPelsWidth == w && m.dmPelsHeight == h).ToList();
            if (modes.Count == 0) return Result.Missing;
            // refresh rate เท่าเดิมถ้ามี ไม่งั้นเอาสูงสุด
            var dm = modes.FirstOrDefault(m => m.dmDisplayFrequency == cur.hz);
            if (dm.dmPelsWidth == 0) dm = modes.OrderByDescending(m => m.dmDisplayFrequency).First();
            dm.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            return ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL
                ? Result.Ok : Result.Failed;
        }

        // กลับเป็นความละเอียดที่ตั้งไว้ใน Windows
        public static void Restore() => ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

        // ---------------------------------------------------------------- หน้าต่างเกม
        // เปลี่ยนจอตอนเกมเปิดอยู่: หน้าต่าง Borderless ของเกมยังค้างขนาดเดิม (ล้น/ไม่เต็มจอ)
        //   → ขยับหน้าต่างให้เต็มจอใหม่ (เหมือนโปรแกรมจัดหน้าต่างทั่วไป ไม่ได้ยุ่งกับข้างในเกม)
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200;

        // ---------------------------------------------------------------- จำตำแหน่งหน้าต่าง
        // เปลี่ยนความละเอียดไปมา Windows จะจัดหน้าต่างใหม่เอง (ที่ย่อ/แบ่งจอไว้กลายเป็นเต็มจอ)
        //   → จำตำแหน่ง/ขนาดทุกหน้าต่างก่อนเปลี่ยน แล้ววางกลับที่เดิมหลังคืนความละเอียด
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT { public int length, flags, showCmd; public int minX, minY, maxX, maxY; public RECT normal; }
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        const int SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3, GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14;

        sealed class Saved { public IntPtr H; public WINDOWPLACEMENT P; public RECT R; }
        static List<Saved> layout;

        public static int SaveLayout(string skipProcess)
        {
            var skip = System.Diagnostics.Process.GetProcessesByName(skipProcess).Select(p => { int id = p.Id; p.Dispose(); return (uint)id; }).ToHashSet();
            skip.Add((uint)Environment.ProcessId);
            var list = new List<Saved>();
            EnumWindows((hw, l) =>
            {
                GetWindowThreadProcessId(hw, out uint pid);
                if (skip.Contains(pid) || !IsWindowVisible(hw) || GetWindow(hw, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                if ((GetWindowLong(hw, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(hw, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
                var p = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(hw, ref p) || !GetWindowRect(hw, out var r)) return true;
                if (r.R - r.L < 50 || r.B - r.T < 50) return true;
                list.Add(new Saved { H = hw, P = p, R = r });
                return true;
            }, IntPtr.Zero);
            layout = list;
            return list.Count;
        }

        // วางกลับที่เดิม (เรียกได้หลายรอบ: Windows อาจจัดหน้าต่างซ้ำอีกทีหลังเปลี่ยนจอ)
        public static int RestoreLayout()
        {
            if (layout == null) return 0;
            int n = 0;
            foreach (var s in layout)
            {
                if (!IsWindow(s.H)) continue;
                var p = s.P;
                if (p.showCmd == SW_SHOWMINIMIZED) continue;           // ย่อลงทาสก์บาร์อยู่ ไม่ต้องยุ่ง
                if (p.showCmd == SW_SHOWMAXIMIZED) { SetWindowPlacement(s.H, ref p); n++; continue; }
                // แบ่งจอ (Snap) ใช้ตำแหน่งจริงบนจอ ไม่ใช่ตำแหน่ง "ปกติ" ใน placement
                var cur = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (GetWindowPlacement(s.H, ref cur) && cur.showCmd == SW_SHOWMAXIMIZED)
                {
                    cur.showCmd = 1;                                  // SW_SHOWNORMAL — เลิกเต็มจอก่อน
                    SetWindowPlacement(s.H, ref cur);
                }
                SetWindowPos(s.H, IntPtr.Zero, s.R.L, s.R.T, s.R.R - s.R.L, s.R.B - s.R.T, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                n++;
            }
            return n;
        }

        public static void ForgetLayout() => layout = null;

        // คืนขนาดหน้าต่างก่อน/หลัง (ไว้ลง log) หรือ null ถ้าไม่เจอหน้าต่างเกม
        public static string FitWindow(string process, int w, int h)
        {
            var pids = System.Diagnostics.Process.GetProcessesByName(process).Select(p => { int id = p.Id; p.Dispose(); return (uint)id; }).ToHashSet();
            if (pids.Count == 0) return null;
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((hw, l) =>
            {
                GetWindowThreadProcessId(hw, out uint pid);
                if (pids.Contains(pid) && IsWindowVisible(hw) && GetWindowRect(hw, out var r))
                {
                    long a = (long)(r.R - r.L) * (r.B - r.T);
                    if (a > bestArea) { bestArea = a; best = hw; }
                }
                return true;
            }, IntPtr.Zero);
            if (best == IntPtr.Zero || bestArea < 640 * 480) return null;
            GetWindowRect(best, out var before);
            SetWindowPos(best, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
            GetWindowRect(best, out var after);
            return $"{before.R - before.L}x{before.B - before.T} → {after.R - after.L}x{after.B - after.T}";
        }
    }
}
