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
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        const uint MOVE = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, ABSOLUTE = 0x8000;
        const long MAGIC_v1 = 0x47424545;               // ลายเซ็นเดิม v1 ("GBEE")
        const long MAGIC = 0;                           // v2: Stealth - ล้าง extra info เป็น 0

        // ควบคุมทุกสเต็ปของ Slate Engine ใน PUBG: Settle, Down, Drag(Nudge), Drop, Up (ms)
        // เพื่อให้ติด 100% แน่นอนในทุกเฟรมเรต โดยใช้เวลาน้อยที่สุด
        struct Timing
        {
            public int Settle; // รอหลังเลื่อนไปช่องของ
            public int Down;   // รอหลังคลิกซ้ายลง
            public int Drag;   // รอหลังสะกิดเริ่มลาก (Micro-nudge trigger)
            public int Drop;   // รอหลังลากถึงจุดปล่อย (กระเป๋า/ตัวละคร)
            public int Up;     // รอหลังปล่อยคลิกซ้าย
            public Timing(int s, int d, int dg, int dp, int u)
            { Settle = s; Down = d; Drag = dg; Drop = dp; Up = u; }
        }

        // เกมรับเมาส์ทีละเฟรม: ทุกขั้น (ชี้ → กด → เริ่มลาก → ชี้ที่กระเป๋า → ปล่อย) ต้องตกคนละเฟรม ไม่งั้นเกมเห็นแค่ขั้นสุดท้าย
        //   แล้วของชิ้นนั้นไม่ถูกลาก. ซิ่งสุดเดิม 3 ms/ขั้น < 1 เฟรม (144 fps = 7 ms) → หลุดบางชิ้น
        //   ขั้นที่ต้องรอนานสุด = ชี้ก่อนกด (Settle) กับชี้ที่กระเป๋าก่อนปล่อย (Drop); ปล่อยแล้วไปชิ้นต่อไปรอสั้นได้
        static readonly Timing[] Speeds = {
            new(18, 16, 16, 18, 12), // 0: ชัวร์ ~80 ms/ชิ้น (เกม ≥ 60 fps, 6 ช่อง ~0.5 วิ)
            new(10, 9, 9, 10, 6),    // 1: เร็ว (แนะนำ) ~44 ms/ชิ้น (เกม ≥ ~110 fps, 6 ช่อง ~0.27 วิ)
            new(6, 5, 5, 6, 4),      // 2: ซิ่ง ~26 ms/ชิ้น (เกมต้อง ≥ ~200 fps ตลอด)
        };

        static int busy;
        public static bool Busy => busy != 0;

        public static void Cancel() => cancel = true;

        // คืน false ถ้ากำลังเก็บอยู่ (กดซ้ำ = หยุด)
        public static bool Run(Settings cfg, Func<bool> stillOk, Action<int> done)
        {
            if (Interlocked.Exchange(ref busy, 1) != 0) { cancel = true; return false; }
            cancel = false;
            int n = Math.Clamp(cfg.LootSlots, 1, 15);
            var sp = Speeds[Math.Clamp(cfg.LootPace, 0, Speeds.Length - 1)];
            double[] a = cfg.LootStart, b = cfg.LootStep, drop = cfg.LootDrop, center = cfg.LootCenter;
            var t = new Thread(() =>
            {
                int moved = 0;
                bool held = false;
                timeBeginPeriod(1);
                try
                {
                    bool topDown = cfg.LootDirection == 1;
                    if (topDown)
                    {
                        // ลากจากช่องบนลงล่าง: เน้นเก็บของชิ้นบนสุด (ปืน, เสื้อเกราะ, หมวก) ก่อน
                        //   เก็บช่องบนแล้วของที่เหลือเลื่อนขึ้นมาแทน → ลากช่องบนสุดทุกรอบ
                        //   (เดิมลากช่อง 1, 2, 3 … ตามลำดับ → ข้ามทีละชิ้น ได้แค่ครึ่งเดียว)
                        for (int i = 0; i < n; i++)
                        {
                            if (cancel || !stillOk()) break;
                            double x = a[0], y = a[1];
                            // 1. ไปช่องของ
                            Move(x, y);
                            Thread.Sleep(sp.Settle);
                            // 2. คลิกซ้ายค้าง
                            Send(LEFTDOWN);
                            held = true;
                            Thread.Sleep(sp.Down);
                            // 3. สะกิดขวาเล็กน้อยเพื่อบังคับ Slate Engine ให้สร้าง DragDrop 100%
                            double nudgeX = Math.Min(x + 0.035, (x + drop[0]) / 2);
                            Move(nudgeX, y);
                            Thread.Sleep(sp.Drag);
                            // 4. ลากเข้าสู่เป้าหมาย (กระเป๋า/ตัวละคร)
                            Move(drop[0], drop[1]);
                            Thread.Sleep(sp.Drop);
                            // 5. ปล่อยคลิกซ้าย
                            Send(LEFTUP);
                            held = false;
                            Thread.Sleep(sp.Up);
                            moved++;
                        }
                    }
                    else
                    {
                        // ลากจากช่องล่างขึ้นบน: ของช่องบนไม่เลื่อนลงมาทับตำแหน่งเดิม
                        for (int i = n - 1; i >= 0; i--)
                        {
                            if (cancel || !stillOk()) break;
                            double x = a[0] + i * (b[0] - a[0]), y = a[1] + i * (b[1] - a[1]);
                            // 1. ไปช่องของ
                            Move(x, y);
                            Thread.Sleep(sp.Settle);
                            // 2. คลิกซ้ายค้าง
                            Send(LEFTDOWN);
                            held = true;
                            Thread.Sleep(sp.Down);
                            // 3. สะกิดขวาเล็กน้อยเพื่อบังคับ Slate Engine ให้สร้าง DragDrop 100%
                            double nudgeX = Math.Min(x + 0.035, (x + drop[0]) / 2);
                            Move(nudgeX, y);
                            Thread.Sleep(sp.Drag);
                            // 4. ลากเข้าสู่เป้าหมาย (กระเป๋า/ตัวละคร)
                            Move(drop[0], drop[1]);
                            Thread.Sleep(sp.Drop);
                            // 5. ปล่อยคลิกซ้าย
                            Send(LEFTUP);
                            held = false;
                            Thread.Sleep(sp.Up);
                            moved++;
                        }
                    }
                    if (stillOk()) Move(center[0], center[1]);
                }
                catch (Exception ex) { Log.Error("loot", ex); }
                finally
                {
                    if (held) Send(LEFTUP);                    // กันปุ่มซ้ายค้างถ้าพังกลางทาง
                    timeEndPeriod(1);
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
            var inp = new[] { new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, flags = flags, extra = IntPtr.Zero } } };
            SendInput(1, inp, Marshal.SizeOf<INPUT>());
        }
    }
}
