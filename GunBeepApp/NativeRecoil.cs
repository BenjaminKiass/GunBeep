using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GunBeep
{
    // ตัวดึงเมาส์ในตัว GunBeep (ไม่ต้องมี G Hub): ทำแบบเดียวกับ spray() ใน recoil.lua ทุกบรรทัด
    //   ตาราง segs (แรงดึงต่อ 10 ms) + base + x → ขยับเมาส์ด้วย SendInput ตามเวลาจริง (ไม่เพี้ยนตามจังหวะ Sleep)
    //   อ่านปุ่มเมาส์ด้วย GetAsyncKeyState เท่านั้น ไม่ดัก/ไม่บล็อกคลิก — ไม่อ่านภาพหรือหน่วยความจำเกม
    //   เปลี่ยนค่าได้ทันที (ปืน / base / x / ตัวคูณ) ไม่มีเฟรม ไม่มีรีสตาร์ท
    public sealed class NativeRecoil : IDisposable
    {
        public sealed class Gun
        {
            public double[] Rc;          // แรงดึงต่อช่วง 10 ms
            public double Base, X;
        }

        const int STEP_MS = 10, RELEASE_GRACE_MS = 30;
        const double X_NUDGE = 1;                        // Alt ค้าง = ดึงซ้ายเพิ่ม / Ctrl ค้าง = ดึงขวาเพิ่ม (เหมือนสคริปต์)
        public const double SHOT_MS = 86;                // ~700 นัด/นาที (ใช้กับ MaxShots)

        const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4;

        volatile Gun[] guns = Array.Empty<Gun>();
        volatile int gunIdx = 1;
        volatile bool active, front, stop;
        double gear = 1, stance = 1;
        readonly Thread thread;

        public volatile bool RequireAds;
        public volatile int MaxShots;
        public volatile bool Spraying;                   // กำลังดึงอยู่ (ไว้โชว์บนหน้าจอ)

        // เรียนรู้: แรงคูณแยกตามช่วงเวลาของการยิงค้าง (ช่วงละ 0.5 วิ, 8 ช่วง = 4 วิ, เกินนั้นใช้ช่วงสุดท้าย)
        public const int BIN_MS = 500, BINS = 8;
        volatile double[] curve = Fill(1);
        public volatile int SprayId;
        public long SprayStartTs;                        // Stopwatch.GetTimestamp() ตอนเริ่มชุด
        // จบชุด: แรงที่มาโครดึงลงในแต่ละช่วง, ยิงนานกี่ ms, สัดส่วนเวลาที่กดคลิกขวาค้าง (เล็ง), เลขชุด
        public event Action<double[], double, double, int> SprayEnded;

        static double[] Fill(double v) { var a = new double[BINS]; Array.Fill(a, v); return a; }
        public void SetCurve(double[] c) => curve = c != null && c.Length == BINS ? (double[])c.Clone() : Fill(1);

        // ค่ากลางของช่วงเชื่อมกันแบบเส้นตรง (ไม่กระโดดตอนข้ามช่วง)
        static double CurveAt(double[] c, double ms)
        {
            double p = ms / BIN_MS - 0.5;
            if (p <= 0) return c[0];
            if (p >= BINS - 1) return c[BINS - 1];
            int i = (int)p;
            return c[i] + (c[i + 1] - c[i]) * (p - i);
        }

        public NativeRecoil()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "NativeRecoil", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        public void SetGuns(Gun[] g) => guns = g ?? Array.Empty<Gun>();
        public void SetGun(int g) => gunIdx = g;
        public void SetActive(bool on) => active = on;    // เปิดมาโครและไม่ได้หยุดชั่วคราว
        public void SetFront(bool f) => front = f;        // หน้าต่าง PUBG อยู่ข้างหน้า
        public void SetGear(double m) => Volatile.Write(ref gear, m);
        public void SetStance(double m) => Volatile.Write(ref stance, m);

        public void Dispose() => stop = true;

        static bool Down(int vk) => GetAsyncKeyState(vk) < 0;

        bool Ready
        {
            get
            {
                var g = guns;
                int i = gunIdx;
                return active && front && i >= 1 && i <= g.Length && g[i - 1]?.Rc?.Length > 0;
            }
        }

        void Run()
        {
            timeBeginPeriod(1);                          // Sleep(1) = 1 ms จริง
            try
            {
                while (!stop)
                {
                    if (Ready && Down(VK_LBUTTON) && (!RequireAds || Down(VK_RBUTTON)))
                    {
                        try { Spraying = true; Spray(); }
                        catch (Exception ex) { Log.Error("native spray", ex); }
                        finally { Spraying = false; }
                        while (!stop && Down(VK_LBUTTON)) Thread.Sleep(5);    // หยุดดึงแล้วแต่ยังกดค้าง = รอปล่อย
                    }
                    Thread.Sleep(1);
                }
            }
            finally { timeEndPeriod(1); }
        }

        void Spray()
        {
            int gi = gunIdx;
            var g = guns[gi - 1];
            double[] rc = g.Rc;
            int n = rc.Length;
            double bas = g.Base;

            SprayStartTs = Stopwatch.GetTimestamp();
            SprayId++;
            var sw = Stopwatch.StartNew();
            var cv = curve;
            var movedY = new double[BINS];
            int loops = 0, adsLoops = 0;
            double last = 0, carryY = 0, carryX = 0;
            double releaseAt = -1;
            string reason;

            while (true)
            {
                double now = sw.Elapsed.TotalMilliseconds;

                bool pending = false;
                if (Down(VK_LBUTTON)) releaseAt = -1;
                else
                {
                    if (releaseAt < 0) releaseAt = now;
                    if (now - releaseAt >= RELEASE_GRACE_MS) { reason = null; break; }
                    pending = true;
                }
                if (stop || !active || !front) { reason = "macro off / paused"; break; }
                int max = MaxShots;
                if (max > 0 && now >= max * SHOT_MS) { reason = "max shots"; break; }
                if (RequireAds && !Down(VK_RBUTTON)) { reason = null; break; }

                loops++;
                if (Down(VK_RBUTTON)) adsLoops++;
                double factor = Volatile.Read(ref gear) * Volatile.Read(ref stance) * CurveAt(cv, now);

                double dt = now - last;
                if (dt > 0 && !pending)
                {
                    double y = 0;
                    int k = (int)Math.Floor(last / STEP_MS) + 1;
                    while (true)
                    {
                        double s0 = (k - 1) * STEP_MS, s1 = k * STEP_MS;
                        double lo = Math.Max(last, s0), hi = Math.Min(now, s1);
                        if (hi > lo) y += (rc[Math.Min(k, n) - 1] + bas) * factor * (hi - lo) / STEP_MS;
                        if (s1 >= now) break;
                        k++;
                    }

                    var cur = guns;                      // ค่า x ปรับระหว่างยิงได้
                    double bx = gi <= cur.Length && cur[gi - 1] != null ? cur[gi - 1].X : g.X;
                    if (Down(VK_LMENU)) bx -= X_NUDGE;
                    if (Down(VK_LCONTROL)) bx += X_NUDGE;
                    double x = bx * dt / STEP_MS;

                    double wantY = y + carryY;
                    int moveY = (int)Math.Floor(wantY + 0.5);
                    carryY = wantY - moveY;

                    double wantX = x + carryX;
                    int moveX = (int)Math.Floor(wantX + 0.5);
                    carryX = wantX - moveX;

                    if (moveX != 0 || moveY != 0) Move(moveX, moveY);
                    movedY[Math.Min(BINS - 1, (int)(now / BIN_MS))] += moveY;
                    last = now;
                }

                Thread.Sleep(1);
            }

            if (reason != null) Log.Info($"native: stopped ({reason}) t={last:0}ms");
            try { SprayEnded?.Invoke(movedY, last, loops == 0 ? 0 : adsLoops / (double)loops, SprayId); }
            catch (Exception ex) { Log.Error("spray ended", ex); }
        }

        // ---------------------------------------------------------------- SendInput (ขยับแบบสัมพัทธ์)
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public MOUSEINPUT mi; }
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        const uint MOVE = 0x1;
        const long MAGIC = 0x47424545;                   // เครื่องหมายเดียวกับปุ่มที่ KeyHook กดเอง

        static readonly int InputSize = Marshal.SizeOf<INPUT>();

        static void Move(int dx, int dy)
        {
            var inp = new[] { new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, flags = MOVE, extra = (IntPtr)MAGIC } } };
            SendInput(1, inp, InputSize);
        }
    }
}
