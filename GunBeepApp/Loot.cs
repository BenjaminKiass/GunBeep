using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GunBeep
{
    // เก็บของเร็ว: ลากของบนพื้น (ช่อง Vicinity) ไปปล่อยที่กระเป๋าทีละช่อง
    //   ทำเฉพาะตอนเปิดกระเป๋า (เคอร์เซอร์โผล่ในเกม) — ขยับเมาส์ + คลิกแทนมือเท่านั้น ไม่ได้อ่านภาพหรือหน่วยความจำเกม
    //   ลากจากช่องล่างขึ้นบน: เก็บช่องล่างแล้วของช่องบนไม่เลื่อน → ไม่ข้ามชิ้น
    public static class Loot
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public MOUSEINPUT mi; }
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

        const uint MOVE = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, ABSOLUTE = 0x8000;
        const long MAGIC = 0x47424545;                   // เครื่องหมายเดียวกับปุ่มที่ KeyHook กดเอง

        static readonly int[][] Delays = { new[] { 45, 30 }, new[] { 28, 18 }, new[] { 16, 10 } };   // [รอหลังขยับ, รอหลังกด/ปล่อย] ms

        static int busy;
        public static bool Busy => busy != 0;

        // คืน false ถ้ากำลังเก็บอยู่ (กดซ้ำ = หยุด)
        public static bool Run(Settings cfg, Func<bool> stillOk, Action<int> done)
        {
            if (Interlocked.Exchange(ref busy, 1) != 0) { cancel = true; return false; }
            cancel = false;
            int n = Math.Clamp(cfg.LootSlots, 1, 15);
            var d = Delays[Math.Clamp(cfg.LootSpeed, 0, 2)];
            double[] a = cfg.LootStart, b = cfg.LootStep, drop = cfg.LootDrop, center = cfg.LootCenter;
            var t = new Thread(() =>
            {
                int moved = 0;
                bool held = false;
                try
                {
                    for (int i = n - 1; i >= 0; i--)
                    {
                        if (cancel || !stillOk()) break;
                        double x = a[0] + i * (b[0] - a[0]), y = a[1] + i * (b[1] - a[1]);
                        Move(x, y); Thread.Sleep(d[0]);
                        Send(LEFTDOWN); held = true; Thread.Sleep(d[1]);
                        Move((x * 2 + drop[0]) / 3, (y * 2 + drop[1]) / 3); Thread.Sleep(d[1]);   // ขยับผ่านกลางทางให้เกมรู้ว่าลาก
                        Move(drop[0], drop[1]); Thread.Sleep(d[0]);
                        Send(LEFTUP); held = false; Thread.Sleep(d[1]);
                        moved++;
                    }
                    if (stillOk()) Move(center[0], center[1]);
                }
                catch (Exception ex) { Log.Error("loot", ex); }
                finally
                {
                    if (held) Send(LEFTUP);                    // กันปุ่มซ้ายค้างถ้าพังกลางทาง
                    Interlocked.Exchange(ref busy, 0);
                    done?.Invoke(moved);
                }
            }) { IsBackground = true, Name = "Loot" };
            t.Start();
            return true;
        }

        static volatile bool cancel;

        // ตำแหน่งเป็นสัดส่วนของจอหลัก (0–1) → พิกัด absolute 0–65535 ของ SendInput
        static void Move(double x, double y)
        {
            int w = Math.Max(2, GetSystemMetrics(0)), h = Math.Max(2, GetSystemMetrics(1));
            int px = (int)Math.Round(Math.Clamp(x, 0, 1) * (w - 1)), py = (int)Math.Round(Math.Clamp(y, 0, 1) * (h - 1));
            Send(MOVE | ABSOLUTE, (int)((px * 65535L + w / 2) / (w - 1)), (int)((py * 65535L + h / 2) / (h - 1)));
        }

        static void Send(uint flags, int dx = 0, int dy = 0)
        {
            var inp = new[] { new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, flags = flags, extra = (IntPtr)MAGIC } } };
            SendInput(1, inp, Marshal.SizeOf<INPUT>());
        }
    }
}
