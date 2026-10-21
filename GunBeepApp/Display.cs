using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace GunBeep
{
    // เปลี่ยนความละเอียดจอหลักตอนเปิดเกม (จอยืดแบบ Borderless) แล้วคืนค่าเดิมตอนปิดเกม
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

        const int ENUM_CURRENT_SETTINGS = -1, CDS_FULLSCREEN = 4, DISP_CHANGE_SUCCESSFUL = 0;
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
    }
}
