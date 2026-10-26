using System;
using System.Runtime.InteropServices;

namespace GunBeep
{
    // ดักปุ่มคีย์บอร์ดทั้งระบบ (รวมปุ่มที่ G Hub ส่งมา)
    //   รันบนเธรดของตัวเอง: ถ้าเธรด UI ค้าง (อ่าน/เขียนไฟล์ G Hub) Windows จะข้าม hook ที่ตอบช้า
    //   → SOCD พลาดจังหวะกด/ปล่อย แล้วปุ่มค้างในเกมได้ — event KeyDown/KeyUp จึงมาจากเธรดนี้ (ผู้ใช้ต้อง Dispatcher เอง)
    public sealed class KeyHook : IDisposable
    {
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint msg; public IntPtr w, l; public uint time; public int x, y; }

        // ปุ่มที่โปรแกรมกดเองจะติดเครื่องหมายนี้ไว้ เพื่อไม่ให้นับซ้ำ
        const ulong MAGIC = 0x47424545;

        public event Action<int> KeyDown;
        public event Action<int> KeyUp;

        readonly HookProc proc;
        IntPtr hook;
        uint threadId;
        readonly bool[] down = new bool[256];

        public KeyHook()
        {
            proc = Callback;
            var ready = new System.Threading.ManualResetEventSlim();
            var t = new System.Threading.Thread(() =>
            {
                threadId = GetCurrentThreadId();
                hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
                ready.Set();
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }      // hook ต้องมี message loop บนเธรดที่ติดตั้ง
                UnhookWindowsHookEx(hook);
            }) { IsBackground = true, Name = "KeyHook", Priority = System.Threading.ThreadPriority.Highest };
            t.Start();
            ready.Wait();
        }

        IntPtr Callback(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0)
            {
                int vk = Marshal.ReadInt32(l);
                ulong extra = (ulong)Marshal.ReadIntPtr(l, 16).ToInt64();
                int msg = (int)w;
                if (vk >= 0 && vk < 256 && extra != MAGIC)
                {
                    bool isDown = msg == 0x100 || msg == 0x104, isUp = msg == 0x101 || msg == 0x105;
                    // SOCD: ปุ่มทิศที่ชนกัน → บล็อกตัวจริงแล้วให้ Socd ส่งแทน (ฟีเจอร์อื่นของ GunBeep ยังเห็นปุ่มตามจริง)
                    bool block = (isDown || isUp) && Socd.Handle(vk, Marshal.ReadInt32(l, 4), (Marshal.ReadInt32(l, 8) & 1) != 0, isDown);
                    if (msg == 0x100 || msg == 0x104)
                    {
                        if (!down[vk]) { down[vk] = true; KeyDown?.Invoke(vk); }
                    }
                    else if (msg == 0x101 || msg == 0x105)
                    {
                        down[vk] = false;
                        KeyUp?.Invoke(vk);
                    }
                    if (block) return (IntPtr)1;
                }
            }
            return CallNextHookEx(hook, n, w, l);
        }

        public static bool IsLockOn(int vk) => (GetKeyState(vk) & 1) != 0;

        public static void Tap(byte vk, byte scan, bool extended)
        {
            uint ext = extended ? 1u : 0u;
            keybd_event(vk, scan, ext, (UIntPtr)MAGIC);
            keybd_event(vk, scan, ext | 2u, (UIntPtr)MAGIC);
        }

        public static void Press(byte vk, byte scan, bool extended, bool down)
        {
            uint flags = (extended ? 1u : 0u) | (down ? 0u : 2u);
            keybd_event(vk, scan, flags, (UIntPtr)MAGIC);
        }

        public void Dispose() => PostThreadMessage(threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }
}
