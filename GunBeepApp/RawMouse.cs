using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GunBeep
{
    // อ่านการเคลื่อนเมาส์จริงแบบ Raw Input (นับเป็น count ของเมาส์ ไม่สนความเร็วเคอร์เซอร์ของ Windows)
    //   RIDEV_INPUTSINK = ได้รับแม้หน้าต่างไม่ได้ถูกเลือก (ตอนอยู่ในเกม)
    //   หน่วยเดียวกับ MoveMouseRelative ของ G Hub → เอาไปตั้งค่าแรงดึงได้ตรงๆ
    public sealed class RawMouse : IDisposable
    {
        // อุปกรณ์, dx, dy, ปุ่มซ้าย: +1 กดลง, -1 ปล่อย, 0 ไม่เปลี่ยน
        //   อุปกรณ์ใช้แยกมือคนกับมาโคร: การขยับจาก G Hub มาจากอุปกรณ์เสมือน (หรือ 0) ไม่ใช่เมาส์ที่กดยิง
        public event Action<IntPtr, int, int, int> Input;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint count, uint size);
        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);

        const int WM_INPUT = 0x00FF;
        const uint RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1;
        const ushort MOUSE_MOVE_ABSOLUTE = 1, LEFT_DOWN = 0x1, LEFT_UP = 0x2;

        static readonly int HeaderSize = 8 + IntPtr.Size * 2;     // dwType, dwSize, hDevice, wParam
        readonly HwndSource src;
        IntPtr buf = IntPtr.Zero;
        uint bufSize;

        public RawMouse()
        {
            // หน้าต่างล่องหนไว้รับข้อความ WM_INPUT
            src = new HwndSource(new HwndSourceParameters("GunBeepRawMouse") { Width = 0, Height = 0, WindowStyle = 0 });
            src.AddHook(Hook);
            var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK, Target = src.Handle } };
            if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                throw new InvalidOperationException("ลงทะเบียนอ่านเมาส์ไม่สำเร็จ (" + Marshal.GetLastWin32Error() + ")");
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_INPUT) return IntPtr.Zero;
            uint size = 0;
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)HeaderSize);
            if (size == 0) return IntPtr.Zero;
            if (size > bufSize)
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal((int)size);
                bufSize = size;
            }
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, (uint)HeaderSize) != size) return IntPtr.Zero;
            if ((uint)Marshal.ReadInt32(buf, 0) != RIM_TYPEMOUSE) return IntPtr.Zero;

            // RAWMOUSE: usFlags(2) pad(2) | usButtonFlags(2) usButtonData(2) | ulRawButtons(4) | lLastX(4) | lLastY(4)
            int o = HeaderSize;
            ushort flags = (ushort)Marshal.ReadInt16(buf, o);
            ushort buttons = (ushort)Marshal.ReadInt16(buf, o + 4);
            int dx = Marshal.ReadInt32(buf, o + 12), dy = Marshal.ReadInt32(buf, o + 16);
            if ((flags & MOUSE_MOVE_ABSOLUTE) != 0) dx = dy = 0;
            int left = (buttons & LEFT_DOWN) != 0 ? 1 : (buttons & LEFT_UP) != 0 ? -1 : 0;
            if (dx != 0 || dy != 0 || left != 0) Input?.Invoke(Marshal.ReadIntPtr(buf, 8), dx, dy, left);
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_REMOVE, Target = IntPtr.Zero } };
            RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            src.RemoveHook(Hook);
            src.Dispose();
            if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
        }
    }
}
