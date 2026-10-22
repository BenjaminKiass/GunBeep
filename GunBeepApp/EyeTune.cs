using System;
using System.Linq;
using System.Windows.Threading;

namespace GunBeep
{
    // จูนด้วยตา: เปิดมาโคร มือนิ่ง ยิงค้างใส่กำแพง → ป้ายบนจอถามว่ารอยกระสุนช่วงนี้ไปทางไหน → ตอบด้วยลูกศร
    //   ถามทีละช่วง: ต้นแม็ก → กลางแม็ก → ท้ายแม็ก → ซ้าย/ขวา (ต้นแม็กก่อน เพราะต้นเพี้ยน ช่วงหลังเพี้ยนตาม)
    //   ขยับทีละ 1.0 ถ้าตอบกลับทางเมื่อไหร่ลดครึ่ง (0.5 → 0.25) = เข้าหาค่าพอดีเร็วและไม่แกว่ง
    //   ตอบ "พอดี" 2 ครั้งติด = ช่วงนั้นเสร็จ ไปช่วงถัดไปเอง
    //   ไม่อ่านภาพจากเกม: คนดูรอยเอง ระบบแค่จับเวลาจากปุ่มยิง (Raw Input)
    public sealed class EyeTune : IDisposable
    {
        public const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_ESC = 0x1B;

        record Part(string Name, string Shots, double From, double To, bool Vertical);

        const double WINDOW = 3.0;                   // วินาทีต่อชุด (≈ 1 แม็ก 30-35 นัด) หลังจากนี้มาโครดึงคงที่
        static readonly Part[] Parts =
        {
            new("ต้นแม็ก", "นัด 1–7", 0, 0.6, true),
            new("กลางแม็ก", "นัด 8–21", 0.6, 1.8, true),
            new("ท้ายแม็ก", "นัด 22 ขึ้นไป", 1.8, WINDOW, true),
            new("ซ้าย / ขวา", "ทั้งแม็ก", 0, WINDOW, false),
        };

        enum St { Ready, Firing, Asking, Sending, Done }

        readonly int gun;                            // 0-based
        readonly string gunName;
        readonly Func<GHub.GunVals> current;        // ค่าปืนนี้ใน GunBeep ตอนนี้
        readonly Action<Calibrator.Fit> apply;      // ใส่ค่าใหม่ + ส่งเข้า G Hub
        readonly Func<bool> busy;                    // กำลังส่งค่าอยู่
        readonly Func<int> activeGun;                // ปืนที่มาโครใช้ (1-3), 0 = ปิด
        readonly Action<string> guide, pop;
        readonly Action<bool> beep;                  // true = ถูก/ไปต่อ, false = เตือน
        public event Action Finished;

        readonly RawMouse raw;
        readonly DispatcherTimer cut = new(), wait = new() { Interval = TimeSpan.FromMilliseconds(300) };
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        St st = St.Ready;
        int part, okCount, lastDir;
        double step, t0;
        IntPtr hand;

        public EyeTune(int gun, string gunName, Func<GHub.GunVals> current, Action<Calibrator.Fit> apply, Func<bool> busy,
                       Func<int> activeGun, Action<string> guide, Action<string> pop, Action<bool> beep)
        {
            this.gun = gun;
            this.gunName = gunName;
            this.current = current;
            this.apply = apply;
            this.busy = busy;
            this.activeGun = activeGun;
            this.guide = guide;
            this.pop = pop;
            this.beep = beep;

            cut.Interval = TimeSpan.FromSeconds(WINDOW);
            cut.Tick += (s, e) =>
            {
                cut.Stop();
                if (st == St.Firing) { guide("✓ ครบแล้ว — ปล่อยนิ้วได้"); beep(true); }
            };
            wait.Tick += (s, e) =>
            {
                if (busy()) return;
                wait.Stop();
                st = St.Ready;
                AskFire();
            };

            raw = RawMouse.Shared;
            raw.Input += OnRaw;
            StartPart(0);
            Log.Info($"eye tune start {gunName}");
        }

        Part P => Parts[part];

        void StartPart(int i)
        {
            part = i;
            okCount = 0;
            lastDir = 0;
            step = P.Vertical ? 1.0 : 0.5;
            AskFire();
        }

        void AskFire()
        {
            if (busy()) { WaitSend(); return; }
            guide($"👁 จูนด้วยตา · {gunName} · ข้อ {part + 1}/{Parts.Length}\n" +
                  "เล็งจุดเดียวบนกำแพง มือนิ่ง (ไม่ต้องดึงเมาส์)\nกดยิงค้างไว้จนป้ายบอกให้ปล่อย");
        }

        void WaitSend()
        {
            st = St.Sending;
            guide("⏳ กำลังใส่ค่าใหม่ให้มาโคร... รอแป๊บ (อย่าเพิ่งยิง)");
            wait.Start();
        }

        void OnRaw(IntPtr dev, int dx, int dy, int left)
        {
            if (left == 1 && st == St.Ready)
            {
                int ag = activeGun();
                if (ag == 0) { guide("⚠ มาโครปิดอยู่ — กดปุ่มเปิดมาโครก่อน แล้วยิงใหม่"); beep(false); return; }
                if (ag - 1 != gun) { guide($"⚠ มาโครใช้ปืน {ag} อยู่ — สลับเป็น {gunName} ก่อน แล้วยิงใหม่"); beep(false); return; }
                st = St.Firing;
                hand = dev;
                t0 = clock.Elapsed.TotalSeconds;
                guide("🔫 ยิงค้างไว้... มือนิ่งๆ");
                cut.Stop();
                cut.Start();
            }
            else if (left == 1 && st == St.Sending)
            {
                pop("รอแป๊บ กำลังใส่ค่าใหม่ (ชุดนี้ไม่นับ)");
            }
            else if (left == -1 && st == St.Firing && dev == hand)
            {
                cut.Stop();
                double held = clock.Elapsed.TotalSeconds - t0;
                if (held < WINDOW - 0.3)
                {
                    st = St.Ready;
                    beep(false);
                    guide($"⚠ ปล่อยเร็วไป ({held:0.0} วิ) — ยิงค้างจนป้ายบอกว่า \"ครบแล้ว\"\nยิงใหม่ได้เลย");
                    return;
                }
                st = St.Asking;
                Ask();
            }
        }

        void Ask()
        {
            string head = $"👁 ข้อ {part + 1}/{Parts.Length} · ดูรอยกระสุน{P.Name} ({P.Shots})";
            guide(P.Vertical
                ? head + "\n⬆ = รอยลอยขึ้น     ⬇ = รอยตกลง     ➡ = พอดีแล้ว"
                : head + "\n⬅ = รอยไปซ้าย     ➡ = รอยไปขวา     ⬆ = ตรงแล้ว");
        }

        // คืน true ถ้าใช้ปุ่มนี้ (ปุ่มลูกศรรับเฉพาะตอนป้ายกำลังถาม)
        public bool Key(int vk)
        {
            if (vk == VK_ESC && st != St.Done) { Stop("ยกเลิกจูนด้วยตาแล้ว"); return true; }
            if (st != St.Asking) return false;
            int dir;                                   // +1 = ต้องดึงเพิ่ม (ลง / ไปขวา), −1 = ลดลง, 0 = พอดี
            if (P.Vertical)
            {
                if (vk == VK_UP) dir = +1;
                else if (vk == VK_DOWN) dir = -1;
                else if (vk == VK_RIGHT) dir = 0;
                else return false;
            }
            else
            {
                if (vk == VK_LEFT) dir = +1;               // รอยไปซ้าย → ดึงไปทางขวาเพิ่ม (x +)
                else if (vk == VK_RIGHT) dir = -1;
                else if (vk == VK_UP) dir = 0;
                else return false;
            }
            Answer(dir);
            return true;
        }

        void Answer(int dir)
        {
            if (dir == 0)
            {
                okCount++;
                beep(true);
                Log.Info($"eye tune {gunName} {P.Name}: ok ({okCount})");
                if (okCount >= 2) { Next(); return; }
                st = St.Ready;
                guide($"👍 {P.Name} พอดี — ยิงยืนยันอีก 1 ชุด\nมือนิ่ง กดยิงค้างจนป้ายบอกให้ปล่อย");
                return;
            }
            okCount = 0;
            if (lastDir != 0 && dir != lastDir) step = Math.Max(P.Vertical ? 0.25 : 0.5, step / 2);   // กลับทาง = ใกล้แล้ว
            lastDir = dir;
            Nudge(dir * step);
            beep(true);
            pop($"{P.Name}: {(P.Vertical ? (dir > 0 ? "ดึงลงเพิ่ม" : "ดึงลงน้อยลง") : (dir > 0 ? "ดึงไปขวาเพิ่ม" : "ดึงไปซ้ายเพิ่ม"))} {GHub.Fmt(step)}");
            WaitSend();
        }

        void Next()
        {
            if (part + 1 < Parts.Length) { StartPart(part + 1); beep(true); return; }
            // ครบทุกช่วง: เกลี่ยรอยต่อระหว่างช่วงให้เรียบ
            var cur = current();
            if (cur != null)
            {
                int len = (int)Math.Round(WINDOW * 100);
                var total = Enumerable.Range(0, len).Select(k => Calibrator.At(cur, k)).ToArray();
                apply(Calibrator.FromTotal(total, cur.X, cur.Base, smooth: true));
            }
            st = St.Done;
            Log.Info($"eye tune done {gunName}");
            guide($"✓ จูนเสร็จแล้ว! {gunName} พร้อมใช้\nอย่าลืมกด \"บันทึก\" โปรไฟล์ในโปรแกรม");
            beep(true);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (s, e) => { t.Stop(); Stop(null); };
            t.Start();
        }

        // ปรับเฉพาะช่วงเวลาของข้อนี้ ขอบช่วงค่อยๆ ไล่ 0.1 วิ (ไม่ให้แรงดึงกระตุกตรงรอยต่อ)
        void Nudge(double amount)
        {
            var cur = current();
            if (cur == null) return;
            int len = (int)Math.Round(WINDOW * 100);       // กราฟจบที่ 3 วิ หลังจากนั้นสคริปต์ดึงค่าสุดท้ายคงที่
            var total = Enumerable.Range(0, len).Select(k => Calibrator.At(cur, k)).ToArray();
            double x = cur.X;
            if (P.Vertical)
            {
                int a = (int)Math.Round(P.From * 100), z = (int)Math.Round(P.To * 100);
                const int RAMP = 10;
                for (int k = 0; k < len; k++)
                {
                    double w = k >= a && k < z ? 1
                             : a > 0 && k < a && k >= a - RAMP ? (k - (a - RAMP)) / (double)RAMP
                             : z < len && k >= z && k < z + RAMP ? 1 - (k - z + 1) / (double)(RAMP + 1)
                             : 0;
                    total[k] = Math.Max(0, total[k] + amount * w);
                }
            }
            else x += amount;
            var f = Calibrator.FromTotal(total, x, cur.Base);
            Log.Info($"eye tune {gunName} {P.Name} {amount:+0.##;-0.##} → base {GHub.Fmt(f.Base)} x {GHub.Fmt(f.X)}");
            apply(f);
        }

        bool stopped;

        public void Stop(string message)
        {
            if (stopped) return;
            stopped = true;
            cut.Stop();
            wait.Stop();
            raw.Input -= OnRaw;
            guide(null);
            if (message != null) pop(message);
            st = St.Done;
            Finished?.Invoke();
        }

        public void Dispose()
        {
            cut.Stop();
            wait.Stop();
            raw.Input -= OnRaw;
        }
    }
}
