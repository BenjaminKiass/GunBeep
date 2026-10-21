using System;
using System.Collections.Generic;
using System.Threading;

namespace GunBeep
{
    // ส่งค่า base / x เข้าสคริปต์ G Hub แบบ real time (สคริปต์ v5+ ตอบกลับด้วย F18)
    //   Right Ctrl เปลี่ยนสถานะ = 1 จังหวะ (จังหวะแรก = เริ่มเฟรม)
    //   Caps Lock สลับก่อนจังหวะนั้น = บิต 1, ไม่สลับ = บิต 0 → จบแล้วคืน Caps Lock ให้เหมือนเดิม
    //   เฟรม 24 บิต: 10 | ปืน(2) | base*2 (8) | x*2+128 (8) | checksum (ปืน+base+x) % 16 (4)
    // ไม่ใช้ Ctrl+Shift (มักเป็นปุ่มสลับภาษา) และไม่ใช้ Shift ล้วน (กดรัวแล้ว Sticky Keys เด้ง)
    public static class LiveLink
    {
        const byte VK_RCONTROL = 0xA3, VK_CAPITAL = 0x14;
        const int GAP_MS = 25;
        public const int FRAME_GAP_MS = 250;    // เงียบระหว่างเฟรม (สคริปต์นับ >150 ms เป็นเฟรมใหม่)

        public static int[] Frame(int gun, double baseVal, double x)
        {
            int b = Math.Clamp((int)Math.Round(baseVal * 2), 0, 255);
            int xv = Math.Clamp((int)Math.Round(x * 2) + 128, 0, 255);
            int chk = (gun + b + xv) % 16;
            var bits = new List<int> { 1, 0 };
            void Put(int v, int n) { for (int i = n - 1; i >= 0; i--) bits.Add((v >> i) & 1); }
            Put(gun, 2); Put(b, 8); Put(xv, 8); Put(chk, 4);
            return bits.ToArray();
        }

        // ใช้เวลาประมาณ 1.3 วินาที — เรียกจากเธรดเบื้องหลัง
        public static void Send(int[] bits)
        {
            bool down = false;
            void Edge() { down = !down; KeyHook.Press(VK_RCONTROL, 0x1D, true, down); }
            void Caps() => KeyHook.Tap(VK_CAPITAL, 0x3A, false);

            int ones = 0;
            try
            {
                Edge();                         // เริ่มเฟรม
                Thread.Sleep(GAP_MS * 2);
                foreach (int bit in bits)
                {
                    if (bit == 1) { Caps(); ones++; }
                    Thread.Sleep(GAP_MS);
                    Edge();
                    Thread.Sleep(GAP_MS);
                }
            }
            finally
            {
                if (down) Edge();               // ปล่อย Right Ctrl (สคริปต์ไม่นับ เฟรมเต็มแล้ว)
                Thread.Sleep(GAP_MS);
                if (ones % 2 == 1) Caps();      // คืน Caps Lock
            }
        }
    }
}
