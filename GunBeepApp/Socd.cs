using System.Collections.Generic;

namespace GunBeep
{
    // SOCD (กดปุ่มทิศตรงข้ามพร้อมกัน เช่น A กับ D) แบบคีย์บอร์ด Snap Tap / Rappy Snappy แต่ทำในโปรแกรม
    //   ดักปุ่มจริงใน KeyHook → ตัดสินว่าเกมควรเห็นปุ่มไหนกดอยู่ → บล็อกปุ่มจริง / กดหรือปล่อยแทน (ติดเครื่องหมาย GunBeep)
    //   โหมด 0 = ปุ่มล่าสุดชนะ, 1 = ปุ่มแรกชนะ, 2 = กดคู่กัน = ไม่ไปทางไหน (Neutral)
    //   Handle มาจากเธรด KeyHook ส่วนตั้งค่ามาจากเธรด UI → ล็อกตัวเดียวกันทั้งหมด
    public static class Socd
    {
        public static int Mode;

        static readonly Dictionary<int, int> other = new();
        static readonly bool[] phys = new bool[256], outp = new bool[256], ext = new bool[256];
        static readonly int[] scan = new int[256];
        static readonly long[] when = new long[256];
        static long seq;
        static bool active;
        static readonly object gate = new();

        public static bool Active => active;

        public static void SetPairs(IEnumerable<int[]> pairs)
        {
            lock (gate) SetPairsLocked(pairs);
        }

        static void SetPairsLocked(IEnumerable<int[]> pairs)
        {
            Release();
            other.Clear();
            if (pairs == null) return;
            foreach (var p in pairs)
            {
                if (p == null || p.Length != 2) continue;
                int a = p[0], b = p[1];
                if (a <= 0 || a > 255 || b <= 0 || b > 255 || a == b || other.ContainsKey(a) || other.ContainsKey(b)) continue;
                other[a] = b;
                other[b] = a;
            }
        }

        public static void SetActive(bool on)
        {
            lock (gate)
            {
                if (on == active) return;
                if (!on) Release();
                active = on;
            }
            Log.Info("socd " + (on ? "on" : "off"));
        }

        // คืนทุกปุ่มให้ตรงกับที่นิ้วกดจริง (ตอนปิด SOCD / ออกจากเกม / เปลี่ยนคู่ปุ่ม)
        static void Release()
        {
            foreach (var k in other.Keys)
                if (outp[k] != phys[k]) Send(k, phys[k]);
        }

        static void Send(int k, bool down)
        {
            KeyHook.Press((byte)k, (byte)scan[k], ext[k], down);
            outp[k] = down;
        }

        static void Want(int k, int o, out bool wk, out bool wo)
        {
            wk = phys[k];
            wo = phys[o];
            if (!(wk && wo)) return;
            bool kLast = when[k] > when[o];
            switch (Mode)
            {
                case 1: wk = !kLast; break;          // ปุ่มแรกชนะ
                case 2: wk = false; wo = false; return; // กลาง: ไม่ไปทางไหน
                default: wk = kLast; break;          // ปุ่มล่าสุดชนะ
            }
            wo = !wk;
        }

        // true = บล็อกปุ่มจริงตัวนี้ไม่ให้ถึงเกม
        public static bool Handle(int vk, int sc, bool extended, bool down)
        {
            lock (gate) return HandleLocked(vk, sc, extended, down);
        }

        static bool HandleLocked(int vk, int sc, bool extended, bool down)
        {
            if (!other.TryGetValue(vk, out int o)) return false;
            scan[vk] = sc;
            ext[vk] = extended;
            if (down && !phys[vk]) when[vk] = ++seq;
            phys[vk] = down;
            if (!active) { outp[vk] = down; return false; }

            Want(vk, o, out bool wk, out bool wo);
            if (!down)
            {
                // ปล่อยปุ่ม: ส่งตัวจริงไปเสมอ — ปล่อยซ้ำไม่เป็นไร แต่ถ้าบล็อกแล้วเกมไม่เคยรู้ว่าปล่อย ตัวละครจะเดินค้าง
                //   (เช่น KeyHook ช้าจนหลุดจังหวะกดลงไป สถานะในนี้เลยไม่ตรงกับเกม)
                outp[vk] = false;
                if (outp[o] != wo) Send(o, wo);
                return false;
            }
            if (outp[o] != wo)
            {
                // ต้องขยับอีกปุ่มด้วย → บล็อกตัวจริงแล้วส่งเองตามลำดับ (ปล่อยก่อน กดทีหลัง)
                if (!wo) Send(o, false);
                if (outp[vk] && !wk) Send(vk, false);
                if (wo) Send(o, true);
                if (!outp[vk] && wk) Send(vk, true);
                return true;
            }
            if (!wk) return true;                    // ปุ่มนี้แพ้ (รวมปุ่มค้างที่ส่งซ้ำ)
            outp[vk] = true;
            return false;
        }
    }
}
