using System;
using System.Runtime.InteropServices;

namespace GunBeep
{
    // ดักปุ่มคีย์บอร์ดทั้งระบบ (รวมปุ่มที่ G Hub ส่งมา)
    public sealed class KeyHook : IDisposable
    {
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern short GetKeyState(int vk);

        // ปุ่มที่โปรแกรมกดเองจะติดเครื่องหมายนี้ไว้ เพื่อไม่ให้นับซ้ำ
        const ulong MAGIC = 0x47424545;

        public event Action<int> KeyDown;

        readonly HookProc proc;
        readonly IntPtr hook;
        readonly bool[] down = new bool[256];

        public KeyHook()
        {
            proc = Callback;
            hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
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
                    if (msg == 0x100 || msg == 0x104)
                    {
                        if (!down[vk]) { down[vk] = true; KeyDown?.Invoke(vk); }
                    }
                    else if (msg == 0x101 || msg == 0x105)
                    {
                        down[vk] = false;
                    }
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

        public void Dispose() => UnhookWindowsHookEx(hook);
    }
}
