using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace GunBeep
{
    // วัดว่ามุมกล้องในเกมขยับเท่าไหร่ระหว่างเล็ง (จากภาพหน้าจอ) — ใช้เรียนรู้แรงดีดจากของจริง แทนการเดาจากมือผู้ใช้อย่างเดียว
    //   ภาพแถบเหนือเป้ากลางจอ ~100 ครั้ง/วินาที → เทียบโปรไฟล์ความสว่างแนวตั้ง/แนวนอนของเฟรมติดกัน = ภาพเลื่อนกี่พิกเซล
    //   (เว้นแถบกลางจอแนวตั้งที่มีเส้นเป้า และไม่ใช้ส่วนล่างที่มีแสงปากกระบอก/ตัวปืน)
    //   px ต่อ count ของเมาส์ (k) เรียนเองจากตอนผู้ใช้ขยับเมาส์ระหว่างเล็งโดยไม่ยิง แยกตามสโคป → ใช้ได้ทุกค่า sens
    //   อ่านภาพหน้าจออย่างเดียว ไม่อ่าน/เขียนหน่วยความจำเกม
    public sealed class ViewTracker
    {
        public static readonly ViewTracker Shared = new();

        public volatile bool Wanted;                 // เกมอยู่หน้า กำลังเล่น (ไม่มีเคอร์เซอร์/ไม่อยู่ในกระเป๋า)
        public volatile string ScopeKey;             // สโคปที่ถืออยู่ (k แยกตามสโคป)
        public double Vertical = 1;                  // ตัวคูณความไวแนวตั้งในเกม (k แนวตั้ง = k แนวนอน × ค่านี้)

        readonly object gate = new();
        // มุมกล้องที่ขยับ (พิกเซล) ทิศเดียวกับเมาส์: + x = หันขวา, + y = ก้มลง
        readonly List<(long ts, float vx, float vy)> views = new();
        readonly List<(long ts, int dx, int dy)> hands = new();
        readonly Dictionary<string, (double k, int n)> kx = new();
        long lastFireTs, calibFrom;
        Thread thread;

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "ViewTracker", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        // การขยับเมาส์ของผู้ใช้ (เมาส์จริง ไม่ใช่ที่โปรแกรมดึง)
        public void Hand(int dx, int dy)
        {
            lock (gate) hands.Add((Stopwatch.GetTimestamp(), dx, dy));
        }

        // px ต่อ count แนวตั้งของสโคปนี้ (0 = ยังไม่รู้ ต้องการตัวอย่าง ≥ 6 ครั้ง)
        public double KY(string scope)
        {
            lock (gate) return scope != null && kx.TryGetValue(scope, out var v) && v.n >= 6 ? v.k * Math.Max(0.1, Vertical) : 0;
        }

        public double KX(string scope)
        {
            lock (gate) return scope != null && kx.TryGetValue(scope, out var v) && v.n >= 6 ? v.k : 0;
        }

        public int Samples(string scope) { lock (gate) return scope != null && kx.TryGetValue(scope, out var v) ? v.n : 0; }

        // มุมกล้องที่ขยับรวมในช่วงเวลา + จำนวนเฟรมที่วัดได้
        public (double vx, double vy, int frames) Sum(long from, long to)
        {
            double sx = 0, sy = 0;
            int n = 0;
            lock (gate)
                foreach (var v in views)
                    if (v.ts > from && v.ts <= to) { sx += v.vx; sy += v.vy; n++; }
            return (sx, sy, n);
        }

        // ---------------------------------------------------------------- จับภาพ + วัดการเลื่อน
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        static bool Down(int vk) => GetAsyncKeyState(vk) < 0;

        void Loop()
        {
            float[] prevRow = null, prevCol = null;
            Bitmap bmp = null;
            int bw = 0, bh = 0;
            while (true)
            {
                try
                {
                    bool ads = Down(0x02), fire = Down(0x01);
                    if (fire) lastFireTs = Stopwatch.GetTimestamp();
                    if (!Wanted || !ads)
                    {
                        prevRow = prevCol = null;
                        Thread.Sleep(15);
                        continue;
                    }
                    var t0 = Stopwatch.GetTimestamp();
                    var scr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                    // แถบเหนือเป้า กว้าง ±0.36H สูง 0.26H (0.30H–0.04H เหนือกลางจอ) — ใช้แค่ซ้ายขวาที่ห่างกลาง ≥ 0.19H
                    //   (กลางจอมีกรอบศูนย์เล็ง Holo/Red Dot ที่สะบัดตามปืน ไม่ได้ขยับตามโลก)
                    int w = (int)(scr.Height * 0.72) & ~1, h = (int)(scr.Height * 0.26) & ~1;
                    int x0 = scr.Width / 2 - w / 2, y0 = scr.Height / 2 - (int)(scr.Height * 0.30);
                    if (bmp == null || bw != w || bh != h) { bmp?.Dispose(); bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb); bw = w; bh = h; prevRow = prevCol = null; }
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x0, y0, 0, 0, new Size(w, h));
                    var (row, col) = Profiles(bmp, (int)(scr.Height * 0.19));
                    if (prevRow != null)
                    {
                        var (sy, qy) = Shift(prevRow, row, 30);
                        var (sx, qx) = ShiftCols(prevCol, col, 30);
                        // ภาพเลื่อนลง (+) = กล้องเงยขึ้น → มุมกล้องทิศเดียวกับเมาส์ = −ภาพเลื่อน; ย่อภาพ 2 เท่า → ×2
                        float vy = qy >= 0.55 ? (float)(-sy * 2) : 0, vx = qx >= 0.55 ? (float)(-sx * 2) : 0;
                        if (qy >= 0.55 || qx >= 0.55)
                            lock (gate)
                            {
                                views.Add((t0, vx, vy));
                                Trim();
                            }
                    }
                    prevRow = row; prevCol = col;
                    Calibrate(t0, fire);
                    double spent = Ms(Stopwatch.GetTimestamp() - t0);
                    if (spent < 9) Thread.Sleep((int)(9 - spent));
                }
                catch (Exception ex) { Log.Error("view tracker", ex); Thread.Sleep(1000); }
            }
        }

        void Trim()
        {
            long old = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 8;
            if (views.Count > 0 && views[0].ts < old) views.RemoveAll(v => v.ts < old);
            if (hands.Count > 0 && hands[0].ts < old) hands.RemoveAll(v => v.ts < old);
        }

        // คอลัมน์: ซ้ายกับขวาของแถบกลางแยกกัน (ต่อกันตรงรอยต่อจะเพี้ยน) → เฉลี่ยตามความมั่นใจ
        static (double s, double q) ShiftCols(float[] a, float[] b, int maxS)
        {
            int half = a.Length / 2;
            var (s1, q1) = Shift(a[..half], b[..half], maxS);
            var (s2, q2) = Shift(a[half..], b[half..], maxS);
            if (q1 < 0.55) return (s2, q2);
            if (q2 < 0.55) return (s1, q1);
            if (Math.Abs(s1 - s2) > 2) return (q1 >= q2 ? s1 : s2, Math.Min(q1, q2));   // ไม่ตรงกัน = ความมั่นใจต่ำ
            return ((s1 * q1 + s2 * q2) / (q1 + q2), Math.Max(q1, q2));
        }

        // ย่อ 2 เท่าเป็นความสว่าง → ค่าเฉลี่ยแต่ละแถว และแต่ละคอลัมน์ (เว้นแถบกลาง centerBand) → อนุพันธ์ (ทนแสงวาบทั้งภาพ)
        //   คอลัมน์ที่คืน = ซ้ายต่อด้วยขวา (ยาวเท่ากัน)
        static (float[] row, float[] col) Profiles(Bitmap b, int centerBand)
        {
            int w = b.Width / 2, h = b.Height / 2, cx = w / 2, band = Math.Max(2, centerBand / 2);
            var rows = new float[h];
            var cols = new float[w];
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var line = new byte[data.Stride];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * 2) * data.Stride, line, 0, data.Stride);
                    float rs = 0; int rn = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 2 * 3;
                        float l = line[i] * 0.11f + line[i + 1] * 0.59f + line[i + 2] * 0.3f;
                        cols[x] += l;
                        if (Math.Abs(x - cx) > band) { rs += l; rn++; }
                    }
                    rows[y] = rn > 0 ? rs / rn : 0;
                }
            }
            finally { b.UnlockBits(data); }
            for (int x = 0; x < w; x++) cols[x] /= h;
            int side = Math.Max(4, cx - band);
            var left = Deriv(cols[..side]);
            var right = Deriv(cols[(w - side)..]);
            return (Deriv(rows), [.. left, .. right]);
        }

        static float[] Deriv(float[] p)
        {
            var d = new float[p.Length - 1];
            for (int i = 0; i < d.Length; i++) d[i] = p[i + 1] - p[i];
            return d;
        }

        // b[i + s] ≈ a[i] → s (ภาพเลื่อนไป + กี่ช่อง) ด้วยสหสัมพันธ์แบบ normalize + ทศนิยมจากพาราโบลา
        static (double s, double q) Shift(float[] a, float[] b, int maxS)
        {
            int n = Math.Min(a.Length, b.Length);
            var c = new double[2 * maxS + 1];
            double best = -2; int bi = maxS;
            for (int s = -maxS; s <= maxS; s++)
            {
                double ab = 0, aa = 0, bb = 0;
                int lo = Math.Max(0, -s), hi = Math.Min(n, n - s);
                if (hi - lo < n / 2) { c[s + maxS] = -1; continue; }
                for (int i = lo; i < hi; i++) { double x = a[i], y = b[i + s]; ab += x * y; aa += x * x; bb += y * y; }
                double v = aa > 1e-6 && bb > 1e-6 ? ab / Math.Sqrt(aa * bb) : -1;
                c[s + maxS] = v;
                if (v > best) { best = v; bi = s + maxS; }
            }
            double sub = 0;
            if (bi > 0 && bi < c.Length - 1)
            {
                double l = c[bi - 1], m = c[bi], r = c[bi + 1], den = l - 2 * m + r;
                if (Math.Abs(den) > 1e-9) sub = Math.Clamp(0.5 * (l - r) / den, -0.5, 0.5);
            }
            return (bi - maxS + sub, best);
        }

        // ---------------------------------------------------------------- k = มุมกล้อง (px) ต่อ count ของเมาส์ แยกสโคป
        //   ทุก 150 ms ที่เล็งอยู่ ไม่ได้ยิง (และพ้นช่วงยิง 0.6 วิ): มือขยับแนวนอน ≥ 12 count → k = ภาพเลื่อน ÷ count
        bool shiftWas;

        void Calibrate(long now, bool fire)
        {
            bool shift = Down(0xA0);
            string sc = ScopeKey == null ? null : ScopeKey + (shift ? "+shift" : "");
            bool shiftChanged = shift != shiftWas;
            shiftWas = shift;
            if (fire || sc == null || shiftChanged) { calibFrom = now; return; }      // สลับ Shift กลางช่วง = ภาพซูม ไม่ใช่เลื่อน
            if (Ms(now - lastFireTs) < 600) { calibFrom = now; return; }
            if (calibFrom == 0) { calibFrom = now; return; }
            if (Ms(now - calibFrom) < 150) return;
            long from = calibFrom;
            calibFrom = now;
            int hx = 0, hy = 0;
            double vx = 0, vy = 0;
            int frames = 0;
            lock (gate)
            {
                foreach (var hnd in hands) if (hnd.ts > from && hnd.ts <= now) { hx += hnd.dx; hy += hnd.dy; }
                foreach (var v in views) if (v.ts > from && v.ts <= now) { vx += v.vx; vy += v.vy; frames++; }
            }
            if (frames < 8) return;
            double sample = 0;
            if (Math.Abs(hx) >= 12 && Math.Abs(hx) >= 2 * Math.Abs(hy)) sample = vx / hx;
            else if (Math.Abs(hy) >= 12 && Math.Abs(hy) >= 2 * Math.Abs(hx)) sample = vy / hy / Math.Max(0.1, Vertical);
            if (sample < 0.02 || sample > 30) return;
            lock (gate)
            {
                var (k, n) = kx.TryGetValue(sc, out var o) ? o : (sample, 0);
                // ค่าเฉลี่ยเคลื่อนที่ (ช่วงแรกเฉลี่ยตรงๆ) ตัดค่าที่ต่างจากเดิมเกิน 2 เท่าเมื่อมีตัวอย่างพอแล้ว
                if (n >= 6 && (sample > k * 2 || sample < k / 2)) return;
                double a = n < 10 ? 1.0 / (n + 1) : 0.1;
                kx[sc] = (k + (sample - k) * a, n + 1);
                if (n + 1 == 6) Log.Info($"view: {sc} k={kx[sc].k:0.000} px/count (ready)");
            }
        }
    }
}
