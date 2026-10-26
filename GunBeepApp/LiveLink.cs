using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace GunBeep
{
    // ส่งค่าเข้าสคริปต์ G Hub แบบ real time (สคริปต์ตอบกลับด้วย F18)
    //   Right Ctrl เปลี่ยนสถานะ = 1 จังหวะ (จังหวะแรก = เริ่มเฟรม)
    //   Caps Lock สลับก่อนจังหวะนั้น = บิต 1, ไม่สลับ = บิต 0 → จบแล้วคืน Caps Lock ให้เหมือนเดิม
    //   เฟรม base/x (v5+): 10 | ปืน(2) | base*2 (8) | x*2+128 (8) | checksum (ปืน+base+x) % 16 (4)
    //   เฟรมกราฟ (v6+):   11 | ปืน(2) | จำนวนก้อน(5) | ก้อนละ แรงดึง*4 (7) + จำนวนแท่ง 100 ms (5) | checksum % 256 (8)
    // ไม่ใช้ Ctrl+Shift (มักเป็นปุ่มสลับภาษา) และไม่ใช้ Shift ล้วน (กดรัวแล้ว Sticky Keys เด้ง)
    public static class LiveLink
    {
        const byte VK_RCONTROL = 0xA3, VK_CAPITAL = 0x14;
        // สคริปต์อ่านสัญญาณทุก ~16 ms (วัดแล้ว: 12 ms หลุด, 18 ms ผ่านทุกครั้ง) → เผื่อไว้ตอนเกมกินเครื่อง
        const int BIT_MS = 28;
        public const int FRAME_GAP_MS = 250;    // เงียบระหว่างเฟรม (สคริปต์นับ >150 ms เป็นเฟรมใหม่)
        public const int SEGS_VERSION = 6;      // สคริปต์ตั้งแต่ v6 รับกราฟได้
        public const int BAR_STEPS = 10;        // 1 แท่ง = 10 ช่วง x 10 ms
        const int MAX_RUNS = 31, MAX_RUN_BARS = 31, MAX_VAL = 127;

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);

        static void Put(List<int> bits, int v, int n) { for (int i = n - 1; i >= 0; i--) bits.Add((v >> i) & 1); }

        public const int CFG_VERSION = 7;       // สคริปต์ตั้งแต่ v7 รับเฟรมตั้งค่า (ปุ่มวนปืน)
        public const int NONUM_VERSION = 16;    // สคริปต์ตั้งแต่ v16 ไม่ใช้ Num Lock: Scroll = เปิด/ปิด, เลขปืนส่งเป็นเฟรมปืน, สคริปต์ตอบปืนด้วย F13–F15
        public const int SHOTS_VERSION = 15;    // สคริปต์ตั้งแต่ v15 รับเฟรมตั้งค่าชนิด 2 (MAX_SHOTS)
        public const int ADS_VERSION = 13;      // สคริปต์ตั้งแต่ v13 รับเฟรมตั้งค่าชนิด 1 (REQUIRE_ADS) — v8–v12 ไม่มีตัวรับค่าสด

        // เฟรมตั้งค่า: 01 | ชนิด(2) | ค่า(8) | 0(8) | (ชนิด+ค่า)%16   ชนิด 0 = ปุ่มวนปืน
        // เฟรมปืน (v16+): 00 | ปืน(2) | (ปืน*5+3)%16 — สั้น 8 บิต สลับปืนได้เร็ว
        public static int[] GunFrame(int gun)
        {
            var bits = new List<int> { 0, 0 };
            Put(bits, gun, 2); Put(bits, (gun * 5 + 3) % 16, 4);
            return bits.ToArray();
        }

        public static int[] CfgFrame(int kind, int value)
        {
            var bits = new List<int> { 0, 1 };
            Put(bits, kind, 2); Put(bits, value, 8); Put(bits, 0, 8); Put(bits, (kind + value) % 16, 4);
            return bits.ToArray();
        }

        public static int[] Frame(int gun, double baseVal, double x)
        {
            int b = Math.Clamp((int)Math.Round(baseVal * 2), 0, 255);
            int xv = Math.Clamp((int)Math.Round(x * 2) + 128, 0, 255);
            int chk = (gun + b + xv) % 16;
            var bits = new List<int> { 1, 0 };
            Put(bits, gun, 2); Put(bits, b, 8); Put(bits, xv, 8); Put(bits, chk, 4);
            return bits.ToArray();
        }

        // กราฟส่งเป็นแท่งละ 100 ms ค่าละเอียด 0.25 → ทำให้ค่าในโปรแกรม/ไฟล์ตรงกับที่ส่งจริง
        public static double[] Quantize(double[] steps)
        {
            if (steps == null) return null;
            int bars = (steps.Length + BAR_STEPS - 1) / BAR_STEPS;
            var q = new double[bars * BAR_STEPS];
            for (int b = 0; b < bars; b++)
            {
                int a = b * BAR_STEPS, z = Math.Min(steps.Length, a + BAR_STEPS);
                double avg = 0;
                for (int i = a; i < z; i++) avg += steps[i];
                avg = Math.Clamp(Math.Round(avg / (z - a) * 4) / 4, 0, MAX_VAL / 4.0);
                for (int i = a; i < a + BAR_STEPS; i++) q[i] = avg;
            }
            return q;
        }

        static List<(int v, int n)> Runs(double[] q)
        {
            var runs = new List<(int v, int n)>();
            for (int b = 0; b < q.Length / BAR_STEPS; b++)
            {
                int v = (int)Math.Round(q[b * BAR_STEPS] * 4);
                if (runs.Count > 0 && runs[^1].v == v && runs[^1].n < MAX_RUN_BARS) runs[^1] = (v, runs[^1].n + 1);
                else runs.Add((v, 1));
            }
            return runs;
        }

        // กราฟที่ส่งได้เสมอ: ก้อน (แท่งค่าเดียวกันติดกัน) เกิน 31 → รวมคู่ที่ค่าใกล้กันที่สุดเข้าด้วยกันจนพอดี
        //   กราฟจากการอัดค่าแต่ละแท่งต่างกันนิดหน่อยเกือบทุกแท่ง จึงต้องย่อแบบนี้ (แรงดึงรวมแทบไม่เปลี่ยน)
        public static double[] Fit(double[] steps)
        {
            var q = Quantize(steps);
            if (q == null) return null;
            var runs = Runs(q);
            while (runs.Count > MAX_RUNS)
            {
                int best = -1;
                double bestCost = double.MaxValue;
                for (int i = 0; i + 1 < runs.Count; i++)
                {
                    var (a, b) = (runs[i], runs[i + 1]);
                    if (a.n + b.n > MAX_RUN_BARS) continue;
                    double cost = Math.Abs(a.v - b.v) * Math.Min(a.n, b.n);
                    if (cost < bestCost) { bestCost = cost; best = i; }
                }
                if (best < 0) break;
                var (x, y) = (runs[best], runs[best + 1]);
                int n = x.n + y.n;
                runs[best] = ((int)Math.Round((x.v * x.n + y.v * y.n) / (double)n), n);
                runs.RemoveAt(best + 1);
            }
            return runs.SelectMany(r => Enumerable.Repeat(r.v / 4.0, r.n * BAR_STEPS)).ToArray();
        }

        public static bool SameGraph(double[] a, double[] b) =>
            a != null && b != null && GHub.SameSteps(Quantize(a), Quantize(b));

        // null = ส่งไม่ได้ (ก้อนเยอะเกิน) → ต้องรีสตาร์ท G Hub แทน
        public static int[] SegsFrame(int gun, double[] steps)
        {
            var runs = Runs(Quantize(steps));
            if (runs.Count < 1 || runs.Count > MAX_RUNS) return null;
            var bits = new List<int> { 1, 1 };
            Put(bits, gun, 2); Put(bits, runs.Count, 5);
            int sum = gun + runs.Count;
            foreach (var (v, n) in runs) { Put(bits, v, 7); Put(bits, n, 5); sum += v + n; }
            Put(bits, sum % 256, 8);
            return bits.ToArray();
        }

        public static int Millis(int[] bits) => (bits.Length + 2) * BIT_MS;

        // เรียกจากเธรดเบื้องหลัง: base/x ~0.7 วิ, กราฟ ~2-10 วิ
        public static void Send(int[] bits)
        {
            bool down = false;
            void Edge() { down = !down; KeyHook.Press(VK_RCONTROL, 0x1D, true, down); }
            void Caps() => KeyHook.Tap(VK_CAPITAL, 0x3A, false);

            int ones = 0;
            timeBeginPeriod(1);              // ให้ Sleep ตรงเวลา (ปกติปัดเป็น 15.6 ms)
            try
            {
                Edge();                         // เริ่มเฟรม
                Thread.Sleep(BIT_MS);
                foreach (int bit in bits)
                {
                    if (bit == 1) { Caps(); ones++; }
                    Edge();
                    Thread.Sleep(BIT_MS);
                }
            }
            finally
            {
                if (down) Edge();               // ปล่อย Right Ctrl (สคริปต์ไม่นับ เฟรมเต็มแล้ว)
                if (ones % 2 == 1) Caps();      // คืน Caps Lock
                timeEndPeriod(1);
            }
        }
    }
}
