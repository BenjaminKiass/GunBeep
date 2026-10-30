using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace GunBeep
{
    // สถานะส่งให้ G Hub ผ่านไฟ Lock (G Hub อ่านได้แค่ไฟพวกนี้):
    //   Scroll ดับ + Num ดับ = มาโครปิด
    //   Scroll ติด + Num ดับ = เปิด ปืน 1
    //   Scroll ดับ + Num ติด = เปิด ปืน 2
    //   Scroll ติด + Num ติด = เปิด ปืน 3
    public partial class MainWindow : Window
    {
        const int GUNS = 3;
        const int VK_NUM = 0x90, VK_SCROLL = 0x91, VK_ESC = 0x1B;

        static readonly Color ON = Color.FromRgb(0x22, 0xC5, 0x5E);
        static readonly Color OFF = Color.FromRgb(0xEF, 0x44, 0x44);

        readonly Settings cfg = Settings.Load();
        readonly SolidColorBrush powerBrush = new SolidColorBrush(OFF);
        readonly Border[] cards = new Border[GUNS];
        readonly Border[] badges = new Border[GUNS];
        readonly TextBlock[] nameTexts = new TextBlock[GUNS];
        readonly TextBox[] nameEdits = new TextBox[GUNS];

        readonly Brush accent, activeBg, cardBg, cardLine, badgeIdle, nameIdle, accentText;
        readonly Brush cardHover = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x4A, 0x80)));

        KeyHook hook;
        System.Windows.Forms.NotifyIcon tray;
        Overlay osd;
        DispatcherTimer lockSettle, saveVals, agentWatch;
        GHub.GunVals[] vals;       // ค่าที่โชว์ / ตั้งใจจะใช้ (เซฟลงไฟล์แล้ว)
        GHub.GunVals[] applied;    // ค่าที่ G Hub ใช้อยู่ในหน่วยความจำตอนนี้
        bool syncingSliders, liveBusy, liveAgain, liveFailed;
        TaskCompletionSource<bool> liveAck;
        DispatcherTimer liveTimer, updateTimer;
        Updater.Release pendingUpdate;
        readonly TextBlock[] valTexts = new TextBlock[GUNS];
        int agentPid;
        bool on, scroll, num, capturing, firstPower = true, hintShown, setupBusy;
        int gun = 1;
        NativeRecoil engine;                      // ตัวขยับเมาส์ในตัว (รุ่นไม่ใช้ G Hub)
        bool Native => cfg.Engine == 0;

        public MainWindow()
        {
            InitializeComponent();
            GHub.Native = Native;
            engine = new NativeRecoil();

            accent = Freeze(new LinearGradientBrush(Color.FromRgb(0x1D, 0x6B, 0xFF), Color.FromRgb(0x5C, 0xE1, 0xFF), 45));
            activeBg = Freeze(new LinearGradientBrush(Color.FromRgb(0x10, 0x2C, 0x5C), Color.FromRgb(0x0A, 0x12, 0x24), 90));
            cardBg = Freeze(new LinearGradientBrush(Color.FromRgb(0x11, 0x1A, 0x2E), Color.FromRgb(0x0A, 0x0F, 0x1C), 90));
            cardLine = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x42)));
            badgeIdle = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x24, 0x40)));
            nameIdle = Freeze(new SolidColorBrush(Color.FromRgb(0x9F, 0xB0, 0xD4)));
            accentText = Freeze(new SolidColorBrush(Color.FromRgb(0x8F, 0xDB, 0xFF)));

            var logo = Branding.LogoImage();
            if (logo != null)
            {
                LogoImg.Source = logo;
                LogoImg.Visibility = Visibility.Visible;
                LogoFallback.Visibility = Visibility.Collapsed;
            }
            // อนิเมชันวนตลอด (ขอบหมุน / โลโก้ / ปุ่ม ON เรืองแสง) กิน CPU เพราะหน้าต่างโปร่งใสต้องวาดใหม่ทั้งบาน
            // → วิ่งเฉพาะตอนหน้าต่างถูกเลือกอยู่ และไม่เกิน 24 เฟรม/วินาที
            Activated += (s, e) => UpdateAmbient();
            Deactivated += (s, e) => UpdateAmbient();
            IsVisibleChanged += (s, e) => UpdateAmbient();
            StateChanged += (s, e) => UpdateAmbient();
            Loaded += (s, e) => AnimateIn();

            if (cfg.Left.HasValue && cfg.Top.HasValue
                && cfg.Left.Value >= 0
                && cfg.Left.Value < SystemParameters.PrimaryScreenWidth - 200
                && cfg.Top.Value >= 0
                && cfg.Top.Value < SystemParameters.PrimaryScreenHeight - 200)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = cfg.Left.Value;
                Top = cfg.Top.Value;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = Math.Max(50, (SystemParameters.PrimaryScreenWidth - 960) / 2);
                Top = Math.Max(50, (SystemParameters.PrimaryScreenHeight - 650) / 2);
            }

            Ring.Stroke = powerBrush;
            Glow.Fill = powerBrush;
            PowerIcon.Foreground = powerBrush;
            PowerText.Foreground = powerBrush;

            gun = Math.Clamp(cfg.Gun, 1, GUNS);
            BuildCards();
            BaseSlider.ValueChanged += (s, e) => SliderMoved("base", e.NewValue);
            XSlider.ValueChanged += (s, e) => { UpdateXBar(); SliderMoved("x", e.NewValue); };
            UpdateHotkeyText();
            UpdateEyeBtn();
            UpdateSlotBtn();
            // ปุ่มเมาส์ (กลาง/Back/Forward) ใช้เป็นปุ่มลัดได้เหมือนคีย์บอร์ด + ลูกกลิ้งไว้สลับช่องปืน
            try
            {
                RawMouse.Shared.Button += vk => OnKey(vk);
                RawMouse.Shared.Wheel += OnWheel;
            RawMouse.Shared.Input += (dev, dx, dy, left) => { if (left != 0) { MagFire(left > 0); if (left < 0) ThrowReleased(); } };
            }
            catch (Exception ex) { Log.Error("raw mouse", ex); }

            SoundSwitch.IsChecked = cfg.Sound;
            TopSwitch.IsChecked = cfg.TopMost;
            Topmost = cfg.TopMost;
            SoundSwitch.Click += (s, e) => { cfg.Sound = SoundSwitch.IsChecked == true; cfg.Save(); };
            TopSwitch.Click += (s, e) => { cfg.TopMost = TopSwitch.IsChecked == true; Topmost = cfg.TopMost; cfg.Save(); };

            // เปิดพร้อม Windows: ค่าอยู่ใน registry (Run) ไม่ได้อยู่ใน settings — ย้ายโฟลเดอร์แล้วแก้ path ให้เอง
            AutoStart.Refresh();
            AutoStartSwitch.IsChecked = AutoStart.Enabled;
            AutoStartSwitch.Click += (s, e) =>
            {
                if (!AutoStart.Set(AutoStartSwitch.IsChecked == true))
                    AutoStartSwitch.IsChecked = AutoStart.Enabled;
            };


            SetupTray();
            osd = new Overlay();
            UpdateOsdButtons();

            // ดูว่า PUBG เปิด/ปิดหรือยัง (เปลี่ยนความละเอียดจอให้)
            //   เกมเปิดอยู่แล้วตอน GunBeep เริ่ม (เช่นอัปเดตกลางเกม: ตัวเก่าปิด Windows คืนจอเอง) → ยืดแล้วขยายหน้าต่างเกมให้พอดี
            UpdateResButton();
            gameWas = GameRunning();
            if (cfg.AutoRes && (cfg.ResAlways || gameWas)) { SwitchRes(); if (gameWas) FitGameSoon(); }
            gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            gameTimer.Tick += (s, e) => WatchGame();
            gameTimer.Start();
            menuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            menuTimer.Tick += (s, e) => WatchMenu();
            menuTimer.Start();

            // เปิดโปรแกรมมาให้มาโครปิดไว้ก่อนเสมอ
            scroll = KeyHook.ReadLockNow(VK_SCROLL);     // อ่านไฟจริง (GetKeyState ของเธรด UI ตอนเพิ่งเปิดอาจยังไม่ตรง)
            num = KeyHook.ReadLockNow(VK_NUM);
            on = false;
            UpdateAdsModeButton();
            UpdateMagMode();
            UpdateShotsText();
            ShotsSlider.Value = cfg.MaxShots > 0 ? cfg.MaxShots : ShotsSlider.Maximum;
            ShotsSlider.ValueChanged += (s, e) => ShotsMoved((int)Math.Round(e.NewValue));
            ShotsBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ShotsTyped(); Keyboard.ClearFocus(); } };
            ShotsBox.LostKeyboardFocus += (s, e) => ShotsTyped();
            ApplySocd();
            UpdateSocdBtn();
            UpdateLootBtn();
            UpdateScopeAutoBtn();
            UpdateCalcBtn();
            UpdateLangBtn();
            ApplyState();
            UpdatePower();
            UpdateGuns();

            // G Hub เปลี่ยนปืนจะสลับไฟสองดวงติดกัน รอให้นิ่งก่อนค่อยอ่าน
            lockSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            lockSettle.Tick += (s, e) => { lockSettle.Stop(); SyncFromLocks(); };

            // เซฟค่าที่จูนลงไฟล์ (รอให้หยุดกดสักครู่ก่อน)
            saveVals = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            saveVals.Tick += (s, e) =>
            {
                saveVals.Stop();
                if (vals == null) return;
                var copy = vals.Select(v => new GHub.GunVals { Base = v.Base, X = v.X }).ToArray();
                QueueFile("save guns", () => GHub.WriteGunValues(copy));
            };

            // เลื่อนหลอดเสร็จ → ส่งค่าเข้า G Hub แบบ real time
            liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            liveTimer.Tick += async (s, e) =>
            {
                liveTimer.Stop();
                if (HasPending && !liveFailed) await ApplyLiveAsync();
            };

            if (Native) liveTimer.Interval = TimeSpan.FromMilliseconds(60);     // ในตัว: ใช้ค่าใหม่แทบทันที

            // G Hub รีสตาร์ทเมื่อไหร่ สคริปต์โหลดค่าจากไฟล์ใหม่ → อ่านค่าตามไปด้วย
            agentWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            agentWatch.Tick += (s, e) =>
            {
                if (Native) return;
                int pid = GHub.AgentPid();
                if (pid != 0 && pid != agentPid) { agentPid = pid; LoadGunValues(); GunAfterRestart(); }
            };
            agentPid = GHub.AgentPid();
            agentWatch.Start();

            hook = new KeyHook();
            hook.KeyDown += vk => Dispatcher.BeginInvoke(new Action(() => OnKey(vk)));
            hook.KeyUp += vk => Dispatcher.BeginInvoke(new Action(() => OnKeyUp(vk)));

            SubTitle.Text = "Recoil Control Panel · v" + Updater.Current;

            // เช็กอัปเดตจาก GitHub ตอนเปิด และทุก 6 ชั่วโมง
            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            updateTimer.Tick += (s, e) => CheckUpdate();
            updateTimer.Start();

            InitPages();
            Loaded += (s, e) => Started();
        }

        bool started;
        void Started()
        {
            if (started) return;
            started = true;
            Sfx.Play(Sfx.Ready, cfg.Sound);
            RefreshGHub();
            LoadGunValues();
            CheckUpdate();
            osd.Configure(cfg.OsdMode, cfg.OsdCorner);
            AutoPower(gameWas);
        }

        // ---------------------------------------------------------------- หยุดชั่วคราวตอนอยู่ในเมนู
        // PUBG ซ่อนเคอร์เซอร์ตอนเล่น และโชว์ตอนเปิดกระเป๋า (Tab) / แผนที่ / เมนู Esc
        //   → เคอร์เซอร์โผล่ หรือหน้าต่างเกมไม่ได้อยู่ข้างหน้า = ไม่ดึง (กดเก็บของ/ลากของแล้วเป้าไม่ลากลง)
        //   ดูแค่สถานะเคอร์เซอร์ของ Windows ไม่ได้อ่านภาพหรือหน่วยความจำเกม
        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        DispatcherTimer menuTimer;
        bool paused;
        uint fgPid;
        bool fgIsGame;

        bool GameInFront()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid != fgPid)
            {
                fgPid = pid;
                try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); fgIsGame = p.ProcessName == "TslGame"; }
                catch { fgIsGame = false; }
            }
            return fgIsGame;
        }

        static bool CursorShown()
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            return GetCursorInfo(ref ci) && (ci.flags & 1) != 0;
        }

        // ---------------------------------------------------------------- ภาษาอังกฤษอัตโนมัติในเกม
        // PUBG อ่านปุ่มตามตัวอักษรของภาษาที่ใช้อยู่ → คีย์บอร์ดภาษาไทยแล้วปุ่ม 1/2/3, F, R ฯลฯ ไม่ทำงาน
        //   เข้าเกม (หรือกลับมาเล่นหลังปิดกระเป๋า/เมนู) = ขอให้หน้าต่างเกมเปลี่ยนเป็น English (US)
        //   ออกจากเกม = คืนภาษาเดิมให้หน้าต่างที่สลับไป — ระหว่างเล่นไม่บังคับซ้ำ (เผื่ออยากพิมพ์แชทไทย)
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadKeyboardLayout(string id, uint flags);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

        bool playingWas;          // เกมอยู่ข้างหน้าและไม่มีเคอร์เซอร์ (กำลังเล่น)
        bool gameFrontWas;
        IntPtr langBefore;        // ภาษาก่อนเข้าเกม (คืนตอนออก), Zero = ไม่ได้เปลี่ยน

        static bool IsEnglish(IntPtr hkl) => ((long)hkl & 0x3FF) == 0x09;     // LANG_ENGLISH (ทุกประเทศ)

        // ภาษาแป้นพิมพ์ของหน้าต่างที่อยู่ข้างหน้า (เกม) เป็นอังกฤษไหม
        static bool GameKeyboardEnglish()
        {
            IntPtr fg = GetForegroundWindow();
            return fg == IntPtr.Zero || IsEnglish(GetKeyboardLayout(GetWindowThreadProcessId(fg, out _)));
        }

        void WatchLanguage(bool front, bool playing)
        {
            if (cfg.AutoEnglish && playing && !playingWas)
            {
                IntPtr fg = GetForegroundWindow();
                uint tid = GetWindowThreadProcessId(fg, out _);
                IntPtr cur = GetKeyboardLayout(tid);
                if (!IsEnglish(cur))
                {
                    IntPtr en = LoadKeyboardLayout("00000409", 0);
                    if (en != IntPtr.Zero && PostMessage(fg, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, en))
                    {
                        if (langBefore == IntPtr.Zero) langBefore = cur;
                        Log.Info($"keyboard → English for PUBG (was {(long)cur & 0xFFFF:X4})");
                    }
                }
            }
            if (!front && gameFrontWas && langBefore != IntPtr.Zero)
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero) PostMessage(fg, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, langBefore);
                Log.Info($"keyboard ← {(long)langBefore & 0xFFFF:X4} (left PUBG)");
                langBefore = IntPtr.Zero;
            }
            playingWas = playing;
            gameFrontWas = front;
        }

        void UpdateLangBtn() => LangSwitch.IsChecked = cfg.AutoEnglish;

        void Lang_Click(object sender, RoutedEventArgs e)
        {
            cfg.AutoEnglish = LangSwitch.IsChecked == true;
            cfg.Save();
            UpdateLangBtn();
            Flash(cfg.AutoEnglish ? "เข้าเกมแล้วสลับเป็นภาษาอังกฤษให้เอง" : "ไม่สลับภาษาให้แล้ว");
        }

        // ---------------------------------------------------------------- นับกระสุนในแม็ก
        // เกมไม่บอกจำนวนกระสุน (อ่านภาพ/หน่วยความจำเกม = เสี่ยงแบน) → นับเองจากเวลากดยิงทุกชุดรวมกัน
        //   ครบ "กระสุนต่อแม็ก" = หยุดดึง (ปิดไฟ Scroll ชั่วคราว) จนกว่าจะรีโหลด:
        //   กด R / แม็กหมดแล้วกดยิงอีก (เกมรีโหลดเอง) = เริ่มนับใหม่
        //   นับแยกตามช่องปืน: เปลี่ยนปืน (1 2 3 / ลูกกลิ้ง) แม็กในเกมไม่ได้เติม → สลับไปนับของช่องนั้นต่อ ไม่ล้างค่า
        const double SHOT_MS = 86;                // ปืนที่ไม่รู้จัก: ~700 นัด/นาที (ตรงกับ SHOT_MS ในสคริปต์)
        readonly double[] magFiredBy = new double[4];   // นัดที่ยิงไปแล้วในแม็กของแต่ละช่อง (ชุดที่ปล่อยแล้ว) — 0 = ไม่ใช่ปืน (ระเบิด/เก็บปืน)
        readonly bool[] magEmptyBy = new bool[4];
        int magSlot = 1;                          // ช่องที่ถืออยู่ตามปุ่มที่กด
        DateTime lastMagWheel;
        DateTime? fireStart;                      // กำลังกดยิงค้างตั้งแต่

        bool magEmpty => magSlot != 0 && magEmptyBy[magSlot];

        double ShotsIn(TimeSpan held) => Math.Floor(held.TotalMilliseconds / GunShotMs(magSlot)) + 1;   // นัดแรกออกทันที

        bool Playing() => GameInFront() && !CursorShown() && !invSession;

        void MagFire(bool down)
        {
            if (down)
            {
                if (!Playing() || magSlot == 0) return;
                if (magEmpty) { ResetMag("empty mag clicked = game reloads"); return; }
                fireStart = DateTime.Now;
            }
            else EndBurst();
        }

        void EndBurst()
        {
            if (fireStart == null) return;
            var held = DateTime.Now - fireStart.Value;
            if (magSlot != 0) magFiredBy[magSlot] += IsSemi(magSlot) ? 1 : ShotsIn(held);     // ปืนเคาะ: คลิกละนัด
            fireStart = null;
        }

        bool TimeCounting => cfg.MagMode != 0;

        double MagNow() => magSlot == 0 ? 0 : magFiredBy[magSlot] + (fireStart != null ? (IsSemi(magSlot) ? 1 : ShotsIn(DateTime.Now - fireStart.Value)) : 0);

        void ResetMag(string why)
        {
            if (magSlot == 0) return;
            if (magFiredBy[magSlot] == 0 && fireStart == null && !magEmpty) return;
            Log.Info($"mag reset slot {magSlot} ({why}) after {MagNow():0} shots");
            magFiredBy[magSlot] = 0;
            fireStart = null;
            magEmptyBy[magSlot] = false;
        }

        // เปลี่ยนอาวุธในมือ: 1–3 = ช่องปืน, 0 = ระเบิด/เก็บปืน (ไม่นับ)
        void MagSlotTo(int s)
        {
            if (s == magSlot) return;
            EndBurst();
            magSlot = s;
            LiveDirty();
            if (Native) PushMults();                  // ปืนในมือเปลี่ยน → เคาะ/ออโต้, ความเร็วยิง, แม็ก ของกระบอกนั้น
            if (s != 0) Log.Info($"mag slot {s}: {magFiredBy[s]:0} shots fired{(magEmptyBy[s] ? " (empty)" : "")}");
        }

        void WatchMenu()
        {
            bool front = GameInFront();
            bool cursor = CursorShown();
            engine?.SetFront(front);
            TrackInventory(front, cursor);
            var vt = ViewTracker.Shared;
            vt.Wanted = Native && on && front && !cursor && !invSession && cfg.Calc?.AutoLearn == true && !cfg.Calc.LearnLocked;   // ค่าคำนวณล้วน = ไม่จับภาพ
            vt.ScopeKey = ScopeKey(slotInfo[Math.Clamp(heldSlot, 1, 2)]);
            vt.Vertical = GameSettings.Current.Vertical;
            WatchLanguage(front, front && !cursor && !invSession);
            WatchScope(front, cursor);
            Socd.SetActive(cfg.Socd && (!cfg.SocdGameOnly || front));
            // (ไม่นับกระสุนข้ามชุดแล้ว: แต่ละครั้งที่กดยิงค้าง ตัวยิงหยุดดึงเองเมื่อครบขนาดแม็กของปืน + 2 นัด — แม็กหมดในเกมก็หยุดยิงเอง)
            bool want = false;
            string why = null;
            if (on && magEmpty) { want = true; why = "mag empty"; }
            else if (on && cfg.HolsterPause && magSlot == 0) { want = true; why = "grenade / melee / holstered"; }
            else if (on && NoPullReason() is string np) { want = true; why = np; }
            else if (cfg.PauseInMenu && on && gameWas)
            {
                if (!GameInFront()) { want = true; why = "game not in front"; }
                else if (CursorShown()) { want = true; why = "cursor shown (inventory/map/menu)"; }
                else if (invSession) { want = true; why = "inventory (dragging item)"; }
            }
            if (want != paused)
            {
                paused = want;
                pauseWhy = why;
                ApplyState();
                Log.Info(paused ? "pause: " + why : "resume");
            }
            CheckScroll();
            LiveTick();
        }

        // ไฟ Scroll Lock จริงต้องตรงกับปุ่มในแอปเสมอ — เคยเจอแอปขึ้น OFF แต่ไฟยังติด มาโครเลยยังดึงอยู่
        //   ทุกครึ่งวินาทีอ่านไฟจริง ถ้าไม่ตรงก็กดแก้ให้ (เว้นช่วงที่เพิ่งกด / ผู้ใช้กด Scroll Lock เอง / กำลังส่งค่า)
        DateTime lastScrollTap, lastScrollCheck;

        void CheckScroll()
        {
            if (!NoNum || liveBusy || setupBusy || lockSettle == null || lockSettle.IsEnabled) return;
            var now = DateTime.Now;
            if (now - lastScrollCheck < TimeSpan.FromMilliseconds(500) || now - lastScrollTap < TimeSpan.FromMilliseconds(400)) return;
            lastScrollCheck = now;
            bool real = KeyHook.ReadLockNow(VK_SCROLL);
            bool want = !Native && on && !paused;      // ในตัว: ไฟ Scroll ต้องดับ (สคริปต์ G Hub จะได้ไม่ดึงซ้ำ)
            if (real == want) { scroll = real; return; }
            KeyHook.Tap(VK_SCROLL, 0x46, false);
            scroll = want;
            lastScrollTap = now;
            Log.Info($"scroll lock was {(real ? "on" : "off")} but macro is {(want ? "on" : "off")} → fixed");
        }

        // ---------------------------------------------------------------- จอยืดตอนเปิดเกม

        DispatcherTimer gameTimer;
        bool gameWas, resSwitched;

        static bool GameRunning()
        {
            var ps = System.Diagnostics.Process.GetProcessesByName("TslGame");
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }

        void WatchGame()
        {
            bool now = GameRunning();
            _ = GameSettings.Current;                 // ไฟล์ตั้งค่าเกมเปลี่ยน → อ่านใหม่ (Changed)
            if (now == gameWas) { KeepRes(); return; }
            gameWas = now;
            Log.Info(now ? "PUBG started" : "PUBG closed");
            AutoPower(now);
            pubgSlotKeys = null;                      // อาจแก้ปุ่มในเกมมา → อ่านใหม่
            GameSettings.Reload();
            Array.Clear(slotInfo);                    // เกมใหม่ ปืนใหม่
            if (now && cfg.AutoRes && !cfg.ResAlways) SwitchRes();
            else if (!now && resSwitched && !cfg.ResAlways) RestoreRes();
            if (!now) ShowDeferred();
        }

        bool ResWanted => cfg.AutoRes && (cfg.ResAlways || gameWas);

        // มีอะไรเปลี่ยนจอกลับเอง (ตื่นจาก sleep, เสียบจอใหม่, ไดรเวอร์รีเซ็ต) → ยืดกลับให้ ลองไม่เกินทุก 10 วิ
        DateTime lastResTry;

        void KeepRes()
        {
            if (!resSwitched || !ResWanted) return;
            var c = Display.Current();
            if (c.w == cfg.GameResW && c.h == cfg.GameResH) return;
            if (DateTime.Now - lastResTry < TimeSpan.FromSeconds(10)) return;
            lastResTry = DateTime.Now;
            Log.Info($"display changed to {c.w}x{c.h} by something else → switch back");
            SwitchRes();
            if (gameWas) FitGameSoon();
        }

        // ตั้งค่าเปลี่ยน → ทำตามทันที (ยืด / เปลี่ยนขนาด / คืนค่าเดิม)
        void ApplyResNow()
        {
            if (ResWanted)
            {
                var c = Display.Current();
                if (!(resSwitched && c.w == cfg.GameResW && c.h == cfg.GameResH)) SwitchRes();
            }
            else if (resSwitched) RestoreRes();
            if (gameWas) FitGameSoon();
        }

        // จอเปลี่ยนตอนเกมเปิดอยู่: ขยายหน้าต่างเกมให้เต็มจอใหม่ (ทำซ้ำอีกรอบเผื่อเกมยังไม่ทันรับ)
        void FitGameSoon()
        {
            int n = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += (s, e) =>
            {
                var c = Display.Current();
                string r = Display.FitWindow("TslGame", c.w, c.h);
                Log.Info($"fit PUBG window to {c.w}x{c.h}: {r ?? "window not found"}");
                if (++n >= 2) t.Stop(); else t.Interval = TimeSpan.FromSeconds(1.5);
            };
            t.Start();
        }

        // ระหว่างเล่นห้ามมีแจ้งเตือนของ Windows เด้งทับเกม — เก็บไว้บอกตอนปิดเกม (ในเกมใช้ป้ายบนจอแทน)
        readonly List<(string title, string text)> deferred = new();

        void Notify(string title, string text, bool warn = false)
        {
            Log.Info($"notify: {title} — {text}");
            if (GameRunning()) { if (!deferred.Contains((title, text))) deferred.Add((title, text)); return; }
            tray?.ShowBalloonTip(4000, title, text,
                warn ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.None);
        }

        void ShowDeferred()
        {
            if (deferred.Count == 0 || tray == null) return;
            var (title, text) = deferred[^1];
            if (deferred.Count > 1) text += $"\n(+ อีก {deferred.Count - 1} เรื่อง ดูในหน้าต่าง GunBeep)";
            deferred.Clear();
            tray.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.None);
        }

        void SwitchRes()
        {
            // ยังเป็นจอปกติ → จำตำแหน่งหน้าต่างไว้ คืนจอแล้ววางกลับที่เดิม (ไม่งั้นที่ย่อ/แบ่งจอไว้กลายเป็นเต็มจอ)
            var desk = Display.Desktop();
            var cur = Display.Current();
            if (!resSwitched && cur.w == desk.w && cur.h == desk.h)
                Log.Info($"remember {Display.SaveLayout("TslGame")} window(s)");
            var r = Display.Switch(cfg.GameResW, cfg.GameResH);
            string res = $"{cfg.GameResW}×{cfg.GameResH}";
            Log.Info($"switch display to {res}: {r}");
            switch (r)
            {
                case Display.Result.Ok:
                    resSwitched = true;
                    break;
                case Display.Result.Missing:
                    resSwitched = false;              // ไม่ลองซ้ำเองทุก 10 วิ
                    PopOsd("✗  ไม่มีความละเอียด " + res);
                    Notify("GunBeep: ไม่มีความละเอียด " + res, "สร้างใน NVIDIA Control Panel → Change resolution → Customize ก่อน", true);
                    break;
                case Display.Result.Failed:
                    resSwitched = false;
                    PopOsd("✗  เปลี่ยนจอเป็น " + res + " ไม่สำเร็จ");
                    Notify("GunBeep", "เปลี่ยนความละเอียดจอไม่สำเร็จ", true);
                    break;
            }
        }

        void RestoreRes()
        {
            Log.Info("restore display");
            Display.Restore();
            resSwitched = false;
            RestoreLayoutSoon();
        }

        // Windows จัดหน้าต่างใหม่หลังเปลี่ยนจอ (บางทีช้า) → วางกลับหลายรอบ
        void RestoreLayoutSoon()
        {
            Display.RestoreLayout();
            int n = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (s, e) =>
            {
                int moved = Display.RestoreLayout();
                if (++n >= 3) { t.Stop(); Log.Info($"window layout restored ({moved})"); }
                else t.Interval = TimeSpan.FromMilliseconds(1000);
            };
            t.Start();
        }

        void UpdateResButton() =>
            ResBtn.Content = cfg.AutoRes ? $"{cfg.GameResW}×{cfg.GameResH} · {(cfg.ResAlways ? "ตลอด" : "ในเกม")}" : "ปิด";

        void Res_Click(object sender, RoutedEventArgs e)
        {
            var m = DarkMenu(ResBtn);
            m.Items.Add(Item((cfg.AutoRes ? "     " : "✓  ") + "ปิด (ไม่เปลี่ยนความละเอียด)", () => SetRes(false, cfg.GameResW, cfg.GameResH)));
            m.Items.Add(Sep());
            m.Items.Add(Item((cfg.ResAlways ? "     " : "✓  ") + "ยืดตอนเปิดเกม (ปิดเกมแล้วคืนค่าเดิม)", () => SetResAlways(false)));
            m.Items.Add(Item((cfg.ResAlways ? "✓  " : "     ") + "ยืดตลอด (ตั้งแต่เปิด GunBeep)", () => SetResAlways(true)));
            m.Items.Add(Sep());
            var desk = Display.Desktop();                    // ค่าจริงของ Windows ไม่ใช่ค่าที่ยืดอยู่
            var cur = desk.w > 0 ? (desk.w, desk.h, 0) : Display.Current();
            var list = Display.Resolutions();
            foreach (var (w, h) in list.Where(r => r.w <= cur.w && r.h <= cur.h && r.w >= 1024).Take(14))
            {
                int ww = w, hh = h;
                bool sel = cfg.AutoRes && w == cfg.GameResW && h == cfg.GameResH;
                string note = w == cur.w && h == cur.h ? "  (ค่าปกติของจอ)" : "";
                m.Items.Add(Item((sel ? "✓  " : "     ") + $"{w}×{h}{note}", () => SetRes(true, ww, hh)));
            }
            if (!list.Any(r => r.w == 1728 && r.h == 1080))
            {
                m.Items.Add(Sep());
                m.Items.Add(Item("ไม่เห็น 1728×1080? สร้างใน NVIDIA Control Panel ก่อน", null, false));
            }
            m.IsOpen = true;
        }

        void SetRes(bool on, int w, int h)
        {
            cfg.AutoRes = on;
            cfg.GameResW = w;
            cfg.GameResH = h;
            cfg.Save();
            UpdateResButton();
            Flash(!on ? "ไม่เปลี่ยนความละเอียดจอแล้ว"
                : cfg.ResAlways ? $"ยืดจอเป็น {w}×{h} ตลอด (ปิด GunBeep แล้วคืนค่าเดิม)"
                : $"เปิด PUBG แล้วจะเปลี่ยนจอเป็น {w}×{h} ให้");
            ApplyResNow();
        }

        void SetResAlways(bool always)
        {
            cfg.ResAlways = always;
            cfg.Save();
            UpdateResButton();
            if (!cfg.AutoRes) { Flash("เลือกความละเอียดในเมนูนี้ด้วย"); return; }
            Flash(always ? $"ยืดจอเป็น {cfg.GameResW}×{cfg.GameResH} ตลอด (ปิด GunBeep แล้วคืนค่าเดิม)"
                         : "ยืดจอเฉพาะตอนเปิด PUBG");
            ApplyResNow();
        }

        // ---------------------------------------------------------------- อัดค่าแรงดึงอัตโนมัติ

        Calibrator calibWin;

        void Calib_Click(object sender, RoutedEventArgs e)
        {
            if (vals == null) { Tell("ยังไม่พบสคริปต์ GunBeep ใน G Hub — กด \"ตั้งค่าอัตโนมัติ\" ก่อน"); return; }
            if (calibWin != null) { calibWin.Activate(); return; }
            calibWin = new Calibrator(cfg.Names.Take(vals.Length).ToArray(), gun, () => on ? gun : 0, InMacro,
                                      g => vals != null && g < vals.Length ? new GHub.GunVals { Base = vals[g].Base, X = vals[g].X, Steps = vals[g].Steps?.ToArray() } : null,
                                      ApplyCalibration,
                                      msg => { PopOsd(msg); Sfx.Play(Sfx.Pick, cfg.Sound); }) { Owner = this };
            calibWin.Closed += (s, a) => calibWin = null;
            calibWin.Show();
        }

        // ---------------------------------------------------------------- AI ตรวจจับปืน / ของแต่ง (Vision)
        VisionWindow visionWin;

        void Vision_Click(object sender, RoutedEventArgs e)
        {
            if (visionWin != null) { visionWin.Activate(); return; }
            visionWin = new VisionWindow(cfg, ApplyVisionResult) { Owner = this };
            visionWin.Closed += (s, a) => visionWin = null;
            visionWin.Show();
        }

        void ApplyVisionResult(int slotIdx, double b, double x, string summary)
        {
            if (vals == null) return;
            int g = slotIdx - 1;
            if (g >= vals.Length) g = gun - 1;

            vals[g].Base = b;
            vals[g].X = x;
            UpdateGuns();
            saveVals.Stop();
            saveVals.Start();
            liveFailed = false;
            liveTimer.Stop();
            liveTimer.Start();

            Flash($"AI: {summary}");
            PopOsd($"AI: {summary}");
            Sfx.Play(Sfx.Pick, cfg.Sound);
        }

        async void TriggerVisionScan(bool manual)
        {
            if (!manual)
            {
                // รอให้อนิเมชันเปิด/ปิดกระเป๋าเรนเดอร์ครบก่อนแคปเจอร์
                await Task.Delay(250);
            }

            var rep = await VisionEngine.ScanAsync(cfg);
            if (visionWin != null && visionWin.IsVisible)
            {
                visionWin.RenderReport(rep);
            }

            if (rep != null && rep.Success && cfg.VisionAutoApply)
            {
                int targetSlot = (slot == 2) ? 2 : 1;
                var res = targetSlot == 1 ? rep.Slot1 : rep.Slot2;
                if (res != null)
                {
                    ApplyVisionResult(targetSlot, res.FinalBase, res.FinalX, res.Summary);
                }
            }
            else if (manual && rep != null && rep.Success)
            {
                Flash("AI สแกนจอสำเร็จ");
                PopOsd("AI: สแกนจอสำเร็จ");
                Sfx.Play(Sfx.Pick, cfg.Sound);
            }
        }

        // ---------------------------------------------------------------- สลับปืนตามช่อง (ปุ่ม 1/2 + ลูกกลิ้ง)
        //   เกมไม่บอกว่าถือปืนอะไร (อ่านภาพ/หน่วยความจำเกม = โกง) → ตามปุ่มที่กดเปลี่ยนช่องแทน
        static readonly string[] SlotNames = { "ปิด", "ปุ่ม 1 · 2", "ปุ่ม 1 · 2 + ลูกกลิ้ง" };
        int slot = 1;
        DateTime lastWheel;

        void UpdateSlotBtn() => SlotBtn.Content = SlotNames[Math.Clamp(cfg.SlotSwitch, 0, 2)];

        void Slot_Click(object sender, RoutedEventArgs e)
        {
            cfg.SlotSwitch = (cfg.SlotSwitch + 1) % 3;
            cfg.Save();
            UpdateSlotBtn();
            Flash(cfg.SlotSwitch == 0 ? "ไม่สลับปืนตามช่อง" : "ในเกมกด 1 = ปืน 1, 2 = ปืน 2" + (cfg.SlotSwitch == 2 ? " · ลูกกลิ้ง = สลับช่อง" : ""));
        }

        void SlotTo(int s)
        {
            slot = s;
            if (!on || vals == null || s > vals.Length || s == gun) return;
            SelectGun(s);
        }

        // ---------------------------------------------------------------- ตรวจสโคปอัตโนมัติ
        // เปิดกระเป๋า → อ่านแผงปืน (เลขช่อง, ชื่อ, สโคป) ทุก 0.4 วิ → จำว่าช่อง 1/2 ติดสโคปอะไร
        // ถือปืนช่องไหน (ตามปุ่มที่กด) → ใช้ชุดค่าตามสโคปของปืนนั้น
        ScopeWindow scopeWin;
        readonly ScopeDetect.Panel[] slotInfo = new ScopeDetect.Panel[3];
        int heldSlot = 1;
        bool invWas, scanBusy;
        // เปิดกระเป๋าจริง = กด Tab/I ตอนกำลังเล่น (ไม่มีเคอร์เซอร์) แล้วเคอร์เซอร์โผล่ภายใน 1.5 วิ
        //   ล็อบบี้ / หน้าตั้งค่า / Esc / แผนที่ มีเคอร์เซอร์แต่ไม่ได้มาจาก Tab → ไม่ตรวจ
        //   ระหว่างลากของ (ถือคลิกซ้าย) เกมซ่อนเคอร์เซอร์ — ยังนับว่าอยู่ในกระเป๋า: ไม่ดึง ไม่นับกระสุน วางของแล้วอ่านต่อเอง
        //   (เดิมเคอร์เซอร์หายตอนลาก = นับว่าปิดกระเป๋า เลยไม่อ่านของแต่งที่เพิ่งใส่จนกว่าจะกด Tab ใหม่)
        //   ปิด = เพิ่งกด Tab/I/Esc แล้วเคอร์เซอร์หาย หรือเคอร์เซอร์หายโดยไม่ได้ถือคลิกซ้ายเกิน 0.6 วิ
        DateTime invKeyAt, invCloseKeyAt, hiddenIdleSince, sessionStartAt, lastHiddenAt;
        readonly string[] lastBadOcr = new string[2];
        bool invSession, sessionWas, cursorPrev;

        void TrackInventory(bool front, bool cursor)
        {
            var now = DateTime.Now;
            if (front && !cursor) lastHiddenAt = now;
            if (!front) invSession = false;
            else if (cursor)
            {
                // กด Tab/I ภายใน 1.5 วิ และก่อนกดเพิ่งเห็นว่ากำลังเล่น (ไม่มีเคอร์เซอร์) — ล็อบบี้ไม่เคยซ่อนเคอร์เซอร์ = ไม่นับ
                //   (ไม่ใช้ "เคอร์เซอร์เพิ่งโผล่" แล้ว: กด Tab ปิด-เปิดรัวๆ เคอร์เซอร์อาจหายแค่เฟรมเดียว ตัวจับเวลา 30 ms มองไม่ทัน)
                if (!invSession && now - invKeyAt < TimeSpan.FromSeconds(1.5) && invKeyAt - lastHiddenAt < TimeSpan.FromSeconds(1.5))
                { invSession = true; invCloseKeyAt = DateTime.MinValue; sessionStartAt = now; }
                hiddenIdleSince = now;
            }
            else if (invSession)
            {
                bool closeKey = now - invCloseKeyAt < TimeSpan.FromMilliseconds(400);    // กดปิดแล้วเปิดใหม่เร็วๆ: เกินนี้ถือว่ากระเป๋ายังเปิดอยู่
                if (!closeKey && KeyHook.GetAsyncKeyState(0x01) < 0) hiddenIdleSince = now;     // กำลังลากของ
                else if (closeKey || now - hiddenIdleSince > TimeSpan.FromMilliseconds(600)) invSession = false;
            }
            cursorPrev = front && cursor;
        }
        DateTime invOpenedAt, lastScan;

        void UpdateScopeAutoBtn() =>
            ScopeAutoBtn.Content = cfg.ScopeAuto ? "เปิด" : "ปิด";

        void ScopeAuto_Click(object sender, RoutedEventArgs e)
        {
            cfg.ScopeAuto = !cfg.ScopeAuto;
            cfg.Save();
            UpdateScopeAutoBtn();
            Flash(cfg.ScopeAuto ? "ตรวจสโคปอัตโนมัติ: เปิดกระเป๋าในเกมครั้งเดียว แล้วเปลี่ยนปืน = ใช้ชุดตามสโคปเอง" : "ปิดตรวจสโคปอัตโนมัติ");
        }

        void ScopeSetup_Click(object sender, RoutedEventArgs e)
        {
            if (scopeWin != null) { scopeWin.Activate(); return; }
            scopeWin = new ScopeWindow(cfg, KeyName, UpdateScopeAutoBtn) { Owner = this };
            scopeWin.Closed += (s, a) => { scopeWin = null; UpdateScopeAutoBtn(); };
            scopeWin.Show();
        }

        // ปุ่มเลือกปืนช่อง 1/2: ตั้งเอง หรืออ่านจากไฟล์ตั้งค่า PUBG (อ่านใหม่ทุกครั้งที่เปิดเกม)
        int[] pubgSlotKeys;
        int[] SlotKeys => cfg.SlotKeys ?? (pubgSlotKeys ??= new[] { GameSettings.Current.Slot1, GameSettings.Current.Slot2 });

        bool locating;
        DateTime lastLocate;

        void WatchScope(bool front, bool cursor)
        {
            if (!cfg.ScopeAuto) return;
            // อ่านได้ตอนอยู่ในกระเป๋าและไม่ได้กดเมาส์ค้าง (ลาก/คลิกขวาใส่ของ) — ปล่อยปุ่มปุ๊บอ่านใหม่ใน 0.08 วิ
            //   แม้เคอร์เซอร์ยังไม่กลับมาหลังวางของ → ใส่ของแล้วกด Tab ปิดเร็วๆ ก็ยังทัน
            bool btn = KeyHook.GetAsyncKeyState(0x01) < 0 || KeyHook.GetAsyncKeyState(0x02) < 0;
            bool inv = front && invSession && !btn;
            var now = DateTime.Now;
            if (inv && !invWas) invOpenedAt = now;
            if (!invSession && sessionWas) ApplyScope("inventory closed");
            invWas = inv;
            sessionWas = invSession;
            if (!inv || scanBusy || locating || now - sessionStartAt < TimeSpan.FromMilliseconds(150) || now - invOpenedAt < TimeSpan.FromMilliseconds(80)
                || now - lastScan < TimeSpan.FromMilliseconds(150)) return;
            // ยังไม่รู้ตำแหน่งแผงปืนของความละเอียดนี้ → หาเองจากชื่อปืน (ไม่ต้องแคป/ลากจุด)
            var size = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Size;
            if (cfg.ScopeCalib != $"{size.Width}x{size.Height}")
            {
                if (now - lastLocate < TimeSpan.FromSeconds(2)) return;
                lastLocate = now;
                locating = true;
                _ = LocateAsync();
                return;
            }
            lastScan = now;
            scanBusy = true;
            _ = ScanScopesAsync();
        }

        async Task LocateAsync()
        {
            try
            {
                if (await ScopeDetect.AutoLocate(cfg))
                {
                    cfg.Save();
                    Array.Clear(slotInfo);
                    PopOsd("✓ เจอแผงปืนแล้ว — ตรวจสโคปอัตโนมัติพร้อม");
                }
            }
            catch (Exception ex) { Log.Error("locate", ex); }
            finally { locating = false; }
        }

        // ผู้ใช้กดปุ่มเลือกสโคปเอง ตอนปืนที่ถือติดสโคปที่ยังไม่รู้จัก (หรือรู้ผิด) → จำว่าสโคปนี้ = ชุดนั้น ครั้งหน้าเลือกเอง
        void LearnScope(int set)
        {
            if (!cfg.ScopeAuto) return;
            var p = slotInfo[Math.Clamp(heldSlot, 1, 2)];
            if (p?.Icon == null || p.Set == set || p.Set == 0) return;
            ScopeDetect.Teach(p.Icon, set, cfg.Names[set - 1]);
            p.Set = set;
            p.ScopeLabel = cfg.Names[set - 1];
            PopOsd($"จำแล้ว: สโคปนี้ = {cfg.Names[set - 1]}");
        }

        async Task ScanScopesAsync()
        {
            try
            {
                var panels = await Task.Run(async () =>
                {
                    int w = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, h = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
                    using var shot = ScopeDetect.CaptureArea(cfg, out var origin);
                    if (shot == null) return null;
                    var res = ScopeDetect.Read(cfg, shot, origin, w, h);
                    var boxes = ScopeDetect.Boxes(cfg, w, h);
                    for (int i = 0; i < 2; i++)
                    {
                        var p = res[i];
                        if (p.Slot == 0) continue;
                        var nb = boxes[i].name;
                        nb.Offset(-origin.X, -origin.Y);
                        using var crop = ScopeDetect.Crop(shot, nb);
                        var (_, gunName, raw) = await ScopeDetect.ReadGun(crop);
                        p.Name = gunName ?? "";                                          // ชื่อจริงของปืน ไม่ใช่ตัวหนังสือที่ OCR อ่านได้
                        if (p.Name == "" && !string.IsNullOrEmpty(raw) && raw != lastBadOcr[i]) { lastBadOcr[i] = raw; Log.Info($"scope scan: panel {i + 1} name unreadable '{raw}'"); }
                    }
                    return res;
                });
                if (panels == null) return;
                // แผงไหนไม่มีป้ายเลข = ช่องปืนนั้นว่าง (อ่านก่อนกันหลอนเปลี่ยน Slot)
                var hadBadge = panels.Select(x => x.Slot != 0).ToArray();
                foreach (var p in panels)
                {
                    if (p.Slot is not (1 or 2)) continue;
                    // กันหลอน: ปืนต้องอ่านชื่อได้เป็นปืนจริง (ชื่อมาจากตัวอักษรบนแผง สกินปืนไม่มีผล)
                    if (RecoilCalc.GunKey(p.Name) == null && (slotInfo[p.Slot] == null || RecoilCalc.GunKey(slotInfo[p.Slot].Name) == null))
                    {
                        Log.Info($"scope scan: slot {p.Slot} ignored (name '{p.Name}' is not a gun)");
                        p.Icon?.Dispose();
                        p.Slot = 0;
                        continue;
                    }
                    if (RecoilCalc.GunKey(p.Name) == null) p.Name = slotInfo[p.Slot].Name;     // อ่านชื่อรอบนี้ไม่ชัด ใช้ชื่อเดิม
                    // ปืนที่เจอครั้งแรก → เพิ่มในหน้าคำนวณแรงดีด (×1) ให้ปรับทีหลังได้
                    if (RecoilCalc.GunKey(p.Name) is string gk && (cfg.Calc ??= new CalcSettings()).Guns.TryAdd(gk, 1)) cfg.Save();
                    var old = slotInfo[p.Slot];
                    // อ่านสโคปไม่ชัดรอบนี้ (เช่นเมาส์ชี้ช่องสโคปอยู่ พื้นเป็นสีขาว) → ใช้สโคปเดิม แต่ของแต่ง/ภาพปืนใช้ของรอบนี้
                    if (p.Set == -1 && old != null && old.Set != -1 && (old.Name == p.Name || p.Name == ""))
                    {
                        p.Set = old.Set; p.ScopeLabel = old.ScopeLabel; p.Score = old.Score;
                        if (p.Name == "") p.Name = old.Name;
                    }
                    if (p.AttUnsure != null && old?.Att != null && old.Name == p.Name)
                        for (int k = 0; k < 4; k++)
                            if (p.AttUnsure[k]) { p.Att[k] = old.Att[k]; p.AttId[k] = old.AttId?[k]; }
                    // ค่าใหม่ต้องเห็นซ้ำ 2 รอบติดกันก่อนเชื่อ (แยกชื่อปืน / สโคป / ของแต่งแต่ละช่อง)
                    //   ภาพที่เก็บไว้ 720 จังหวะ: 11–14% ของการเปลี่ยนเป็นการอ่านแวบรอบเดียวแล้วกลับ (เกมวาดแผงใหม่/ลากของ)
                    if (old != null)
                    {
                        if (!Confirmed(p.Slot, "name", old.Name, p.Name)) p.Name = old.Name;
                        if (!Confirmed(p.Slot, "scope", $"{old.Set}|{old.ScopeLabel}", $"{p.Set}|{p.ScopeLabel}"))
                        { p.Set = old.Set; p.ScopeLabel = old.ScopeLabel; p.Score = old.Score; }
                        if (p.Att != null && old.Att != null)
                            for (int k = 0; k < 4; k++)
                                if (!Confirmed(p.Slot, "att" + k, $"{old.Att[k]}|{old.AttId?[k]}", $"{p.Att[k]}|{p.AttId?[k]}"))
                                { p.Att[k] = old.Att[k]; if (p.AttId != null) p.AttId[k] = old.AttId?[k]; }
                    }
                    string attSig(ScopeDetect.Panel x) => x?.Att == null ? "" : string.Join(",", Enumerable.Range(0, 4).Select(k => x.Att[k] ? x.AttId?[k] ?? "?" : "-"));
                    if (old == null || old.Set != p.Set || old.Name != p.Name) Log.Info("scope scan: " + p + " att " + attSig(p));
                    else if (attSig(old) != attSig(p)) Log.Info($"scope scan: slot {p.Slot} {p.Name} attachments {attSig(old)} → {attSig(p)}");
                    old?.Icon?.Dispose();
                    if (old?.AttIcons != null) foreach (var ai in old.AttIcons) ai?.Dispose();
                    slotInfo[p.Slot] = p;
                }
                foreach (var p in panels) if (p.Slot is not (1 or 2)) p.Icon?.Dispose();
                // ช่องว่าง (เห็นซ้ำ 2 รอบ) → ลืมปืนเก่าของช่องนั้น → ถือช่องนั้นอยู่ = ไม่ดึง
                for (int i = 0; i < 2 && i < hadBadge.Length; i++)
                {
                    int s = i + 1;
                    if (hadBadge[i]) { slotEmpty[s] = false; continue; }
                    if (slotInfo[s] != null)
                    {
                        if (!Confirmed(s, "empty", "gun", "empty")) continue;
                        Log.Info($"scope scan: slot {s} empty (was {slotInfo[s].Name})");
                        slotInfo[s].Icon?.Dispose();
                        if (slotInfo[s].AttIcons != null) foreach (var ai in slotInfo[s].AttIcons) ai?.Dispose();
                        slotInfo[s] = null;
                    }
                    slotEmpty[s] = true;
                }
                ApplyScope("scan");
            }
            catch (Exception ex) { Log.Error("scope scan", ex); }
            finally { scanBusy = false; }
        }

        string lastScopeSeen;

        // ค่าที่รอยืนยัน: ช่อง:ส่วน → ค่าใหม่ที่เห็นรอบก่อน
        readonly Dictionary<string, string> pendingRead = new();

        bool Confirmed(int slot, string field, string oldV, string newV)
        {
            string k = slot + ":" + field;
            if (oldV == newV) { pendingRead.Remove(k); return true; }
            if (pendingRead.TryGetValue(k, out var pv) && pv == newV) { pendingRead.Remove(k); return true; }
            pendingRead[k] = newV;                               // เห็นครั้งแรก → รอรอบหน้า
            return false;
        }

        // ใช้ชุดค่าตามสโคปของปืนที่ถืออยู่ + คำนวณตัวคูณ ปืน × สโคป × ของแต่ง
        void ApplyScope(string why)
        {
            if (!cfg.ScopeAuto || vals == null) return;
            var p = slotInfo[Math.Clamp(heldSlot, 1, 2)];
            if (p == null || p.Set == -1) return;                // ไม่รู้จักสโคป = ไม่เปลี่ยน
            var (set, scopeMult, via, mult, parts) = Plan(p);
            bool setChanged = set >= 1 && set <= vals.Length && set != gun;
            bool multChanged = LiveLink.MultCode(mult) != LiveLink.MultCode(multWant[0]);
            multWant[0] = mult;
            LiveDirty();
            // สโคปเปลี่ยนแต่ค่าเท่าเดิม (เช่น Holo ↔ Red Dot = 1x ความไวเดียวกัน) → ยังบอกบนป้ายว่าตรวจเจอแล้ว
            string seen = $"{heldSlot}|{p.Name}|{p.Set}|{p.ScopeLabel}";
            bool scopeChanged = seen != lastScopeSeen;
            lastScopeSeen = seen;
            if (!setChanged && !multChanged)
            {
                if (scopeChanged && why == "scan")
                    PopOsd($"{(string.IsNullOrEmpty(p.Name) ? $"ปืนช่อง {heldSlot}" : p.Name)} · {(p.Set == 0 ? "ศูนย์เหล็ก" : ScopeName(p.ScopeLabel))} (ค่าเท่าเดิม)");
                return;
            }
            Log.Info($"scope auto ({why}): slot {heldSlot} {p.Name} {p.ScopeLabel} → set {set}{(via != null && scopeMult != 1 ? $" via {via}" : "")} ×{mult:0.000} [{parts}]");
            if (setChanged) SelectGun(set);
            PushMults();
            string name = string.IsNullOrEmpty(p.Name) ? $"ปืนช่อง {heldSlot}" : p.Name;
            PopOsd($"{name} · {(p.Set == 0 ? "ศูนย์เหล็ก" : ScopeName(p.ScopeLabel))}{(Math.Abs(mult - 1) > 0.005 ? $"  ×{mult:0.00}" : "")}");
        }

        CalcWindow calcWin;

        void UpdateCalcBtn() => CalcBtn.Content = (cfg.Calc ??= new CalcSettings()).On ? "เปิด" : "ปิด";

        void Calc_Click(object sender, RoutedEventArgs e)
        {
            cfg.Calc ??= new CalcSettings();
            cfg.Calc.On = !cfg.Calc.On;
            cfg.Save();
            CalcChanged();
            Flash(cfg.Calc.On ? "คำนวณแรงดีด: ปืน × สโคป × ของแต่ง × ย่อ/หมอบ (ส่งให้ G Hub เอง)" : "ปิดคำนวณแรงดีด — ใช้ตารางในการ์ดตรงๆ");
        }

        void CalcSetup_Click(object sender, RoutedEventArgs e)
        {
            if (calcWin != null) { calcWin.Activate(); return; }
            calcWin = new CalcWindow(cfg, CalcChanged) { Owner = this };
            calcWin.Closed += (s, a) => calcWin = null;
            calcWin.Show();
        }

        // ตั้งค่าเปลี่ยน → คำนวณใหม่แล้วส่ง
        void CalcChanged()
        {
            UpdateCalcBtn();
            var c = cfg.Calc;
            multWant[1] = !c.On ? 1 : stance == 1 ? 1 + c.CrouchPct / 100 : stance == 2 ? 1 + c.PronePct / 100 : 1;
            if (!c.On) multWant[0] = 1;
            ApplyScope("settings");
            PushMults();
        }

        // ---------------------------------------------------------------- ตัวคูณส่งให้สคริปต์ (v19+)
        // [0] = ปืน×สโคป×ของแต่ง, [1] = ท่าทาง — ส่งเฉพาะค่าที่เปลี่ยน ส่งทีละเฟรม (~0.7 วิ)
        readonly double[] multWant = { 1, 1 };
        readonly int[] multSent = { LiveLink.MultCode(1), LiveLink.MultCode(1) };
        bool multPushing, multOldTold;

        async void PushMults()
        {
            if (Native) { engine.SetGear(multWant[0] * FastGear()); engine.SetStance(multWant[1]); engine.SetBreath(HeldBreath()); engine.SetCurve(HeldCurve()); engine.SetXBias(HeldXBias()); engine.MaxShots = EffectiveMaxShots; engine.SetShotMs(GunShotMs(magSlot)); PushSemi(); LiveDirty(); return; }
            if (multPushing) return;
            if (scriptVersion < LiveLink.GEAR_VERSION)
            {
                if (!multOldTold && (Math.Abs(multWant[0] - 1) > 0.01 || Math.Abs(multWant[1] - 1) > 0.01))
                {
                    multOldTold = true;
                    Flash("สคริปต์ใน G Hub ยังเป็นรุ่นเก่า — กด \"ตั้งค่าอัตโนมัติ\" เพื่อใช้ตัวคูณแรงดีด");
                }
                return;
            }
            multPushing = true;
            try
            {
                for (int round = 0; round < 6; round++)
                {
                    int sub = LiveLink.MultCode(multWant[0]) != multSent[0] ? 0 : LiveLink.MultCode(multWant[1]) != multSent[1] ? 1 : -1;
                    if (sub < 0) break;
                    for (int i = 0; i < 50 && (liveBusy || setupBusy); i++) await Task.Delay(100);
                    if (liveBusy || setupBusy) break;
                    int code = LiveLink.MultCode(multWant[sub]);
                    liveBusy = true;
                    bool ok;
                    try { ok = await SendFrameAsync(LiveLink.MultFrame(sub, code / 64.0)); }
                    finally { liveAck = null; liveBusy = false; }
                    Log.Info($"live {(sub == 0 ? "gear" : "stance")} ×{code / 64.0:0.000}: {(ok ? "ok" : "no ack")}");
                    if (!ok) break;
                    multSent[sub] = code;
                    await Task.Delay(LiveLink.FRAME_GAP_MS);
                }
            }
            finally { multPushing = false; }
        }

        // ---------------------------------------------------------------- ท่าทาง (ยืน / ย่อ / หมอบ)
        // ดูปุ่มในเกม (อ่านจากไฟล์ตั้งค่า PUBG ถ้าได้ ไม่งั้น C / Z) — กดสลับแบบ toggle ของ PUBG, Space = ลุกยืน
        int stance;                                   // 0 ยืน, 1 ย่อ, 2 หมอบ
        int[] stanceKeys;

        void StanceKey(int vk)
        {
            var c = cfg.Calc;
            // นับตอนเกมอยู่หน้าและไม่มีเคอร์เซอร์ (ไม่ใช้ Playing(): หลังปิดกระเป๋า 0.6 วิแรกยังนับว่าอยู่ในกระเป๋า → ปุ่ม C/Z ตอนนั้นหาย ท่าไม่ตรงเกม)
            if (c == null || !c.On || !GameInFront() || CursorShown()) return;
            stanceKeys ??= ReadStanceKeys();
            int was = stance;
            bool Held(int k) => KeyHook.GetAsyncKeyState(k) < 0;
            if (vk == stanceKeys[0]) stance = stance == 1 ? 0 : 1;
            else if (vk == stanceKeys[1]) stance = stance == 2 ? 0 : 2;
            else if (vk == 0x20) stance = stance == 2 ? 1 : 0;        // Space: หมอบ → ย่อ, ย่อ → ยืน
            // ย่ออยู่แล้ววิ่ง (Shift ซ้าย + W ไม่ได้เล็ง) = เกมให้ลุกยืน (Shift ตอนเล็ง = กลั้นหายใจ ไม่ลุก)
            else if (stance == 1 && !Held(0x02) && ((vk == 0xA0 && Held(0x57)) || (vk == 0x57 && Held(0xA0)))) stance = 0;
            else return;
            if (stance == was) return;
            multWant[1] = stance == 1 ? 1 + c.CrouchPct / 100 : stance == 2 ? 1 + c.PronePct / 100 : 1;
            Log.Info($"stance {(stance == 0 ? "stand" : stance == 1 ? "crouch" : "prone")} ×{multWant[1]:0.00}");
            if (on) PushMults();
        }

        static int[] ReadStanceKeys()
        {
            int Key(string action, int def)
            {
                try
                {
                    string f = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                      "TslGame", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
                    string s;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var r = new StreamReader(fs)) s = r.ReadToEnd();
                    var m = System.Text.RegularExpressions.Regex.Match(s, $@"ActionName=""{action}"",Keys=\(\(Key=([A-Z])\)");
                    return m.Success ? m.Groups[1].Value[0] : def;
                }
                catch { return def; }
            }
            return new[] { GameSettings.Current.Crouch, GameSettings.Current.Prone };
        }

        void HeldSlotTo(int s, string why)
        {
            if (heldSlot == s) return;
            heldSlot = s;
            ApplyScope(why);
        }

        void OnWheel(int delta)
        {
            if (Playing() && DateTime.Now - lastMagWheel > TimeSpan.FromMilliseconds(180))   // หมุนหลายแก๊กติดกัน = สลับครั้งเดียว
            {
                lastMagWheel = DateTime.Now;
                MagSlotTo(magSlot == 1 ? 2 : 1);                // ลูกกลิ้ง = สลับปืนหลัก/รอง (โดยประมาณ)
            }
            if (cfg.ScopeAuto)
            {
                if (Playing() && DateTime.Now - lastWheel > TimeSpan.FromMilliseconds(180))
                {
                    lastWheel = DateTime.Now;
                    HeldSlotTo(heldSlot == 1 ? 2 : 1, "wheel");
                }
                return;                                          // ตรวจสโคปเลือกชุดให้แทน "สลับปืนตามช่อง"
            }
            if (cfg.SlotSwitch != 2 || !on || capturing) return;
            var now = DateTime.Now;
            if (now - lastWheel < TimeSpan.FromMilliseconds(180)) return;    // หมุนหลายแก๊กติดกัน = เปลี่ยนครั้งเดียว
            lastWheel = now;
            SlotTo(slot == 1 ? 2 : 1);
        }

        // ---------------------------------------------------------------- จูนด้วยตา

        EyeTune eye;

        void UpdateEyeBtn() =>
            EyeBtn.Content = eye == null ? "👁   จูนด้วยตา  (ง่ายสุด · ตอบด้วยปุ่มลูกศร)" : "■   หยุดจูนด้วยตา";

        void Eye_Click(object sender, RoutedEventArgs e)
        {
            if (eye != null) { eye.Stop("หยุดจูนด้วยตาแล้ว"); return; }
            if (vals == null || gun > vals.Length) { Tell("ยังไม่พบสคริปต์ GunBeep ใน G Hub — กด \"ตั้งค่าอัตโนมัติ\" ก่อน"); return; }
            if (scriptVersion < LiveLink.SEGS_VERSION)
            {
                Tell("สคริปต์ใน G Hub ยังเป็นรุ่นเก่า — กด \"ตั้งค่าอัตโนมัติ\" ก่อน (นอกเกม, G Hub รีสตาร์ท ~10 วิ)");
                return;
            }
            if (!on) ToggleOn();
            int g = gun - 1;
            try
            {
                eye = new EyeTune(g, cfg.Names[g],
                    () => vals != null && g < vals.Length ? new GHub.GunVals { Base = vals[g].Base, X = vals[g].X, Steps = vals[g].Steps?.ToArray() } : null,
                    f => ApplyCalibration(g, f),
                    () => liveBusy || setupBusy || (HasPending && !liveFailed),
                    () => on ? gun : 0,
                    t => osd?.Guide(t),
                    m => PopOsd(m),
                    ok => Sfx.Play(ok ? Sfx.Pick : Sfx.Down, cfg.Sound));
            }
            catch (Exception ex) { Tell("เริ่มจูนด้วยตาไม่ได้: " + ex.Message); return; }
            eye.Finished += () => { var t = eye; eye = null; t?.Dispose(); UpdateEyeBtn(); };
            UpdateEyeBtn();
            Flash("เริ่มจูนด้วยตาแล้ว — กลับเข้าเกม ดูป้ายมุมจอแล้วทำตามได้เลย");
        }

        // ค่าที่สคริปต์ใน G Hub ใช้ยิงอยู่จริงตอนนี้ (ไว้ให้โหมดจูนละเอียดรู้ว่ามาโครดึงไปเท่าไหร่) — ยังส่งไม่เสร็จ = null
        GHub.GunVals InMacro(int g)
        {
            if (liveBusy || HasPending || applied == null || appliedSteps == null || g >= applied.Length || g >= appliedSteps.Length
                || appliedSteps[g] == null) return null;
            return new GHub.GunVals { Base = applied[g].Base, X = applied[g].X, Steps = appliedSteps[g].ToArray() };
        }

        void ApplyCalibration(int g, Calibrator.Fit f)
        {
            if (vals == null || g >= vals.Length) return;
            vals[g].Base = f.Base;
            vals[g].X = f.X;
            vals[g].Steps = f.Steps.ToArray();
            UpdateGuns();
            saveVals.Stop();
            saveVals.Start();
            liveFailed = false;
            liveTimer.Stop();
            liveTimer.Start();
            Flash($"{cfg.Names[g]}: base {GHub.Fmt(f.Base)} · x {GHub.Fmt(f.X)} + กราฟใหม่");
            PopOsd($"ใช้ค่าที่อัด: base {GHub.Fmt(f.Base)} x {GHub.Fmt(f.X)}");
        }

        // ---------------------------------------------------------------- ป้ายบนจอเกม

        void UpdateOsdButtons()
        {
            OsdModeBtn.Content = Overlay.ModeNames[Math.Clamp(cfg.OsdMode, 0, 2)];
            OsdCornerBtn.Content = Overlay.CornerNames[Math.Clamp(cfg.OsdCorner, 0, 3)];
            OsdCornerBtn.IsEnabled = cfg.OsdMode != Overlay.MODE_OFF;
            OsdCornerBtn.Opacity = OsdCornerBtn.IsEnabled ? 1 : 0.45;
        }

        void OsdMode_Click(object sender, RoutedEventArgs e)
        {
            cfg.OsdMode = (cfg.OsdMode + 1) % 3;
            cfg.Save();
            UpdateOsdButtons();
            UpdateOsd();
            osd.Configure(cfg.OsdMode, cfg.OsdCorner);
        }

        void OsdCorner_Click(object sender, RoutedEventArgs e)
        {
            cfg.OsdCorner = (cfg.OsdCorner + 1) % 4;
            cfg.Save();
            UpdateOsdButtons();
            osd.Configure(cfg.OsdMode, cfg.OsdCorner);
        }

        // อัปเดตข้อมูลในป้าย (ไม่เด้งโชว์) — เรียกจาก UpdateGuns ทุกครั้ง
        void UpdateOsd()
        {
            if (osd == null) return;
            string v = vals != null && gun <= vals.Length
                ? $"base {GHub.Fmt(vals[gun - 1].Base)} · x {GHub.Fmt(vals[gun - 1].X)}"
                : "base – · x –";
            osd.SetState(on, gun, cfg.Names[gun - 1], v);
        }

        void PopOsd(string message = null)
        {
            UpdateOsd();
            osd?.Pop(message);
        }

        // เปิดตอนเข้า Windows (--tray): ไม่โชว์หน้าต่าง อยู่ที่ถาดอย่างเดียว
        public void StartInTray()
        {
            hintShown = true;
            Started();
        }

        static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

        // ---------------------------------------------------------------- สถานะ (ไฟ Lock)

        int DecodeState() => scroll ? (num ? 3 : 1) : (num ? 2 : 0);

        // ตั้งไฟให้ตรงกับ on/gun ตอนนี้ (เปิดดวงที่ต้องติดก่อน จะได้ไม่ผ่านสถานะ "ปิด" ระหว่างสลับปืน)
        // สคริปต์ v16+: ไม่แตะ Num Lock (ใน PUBG = วิ่งอัตโนมัติ) — Scroll Lock = เปิด/ปิด, เลขปืนส่งเป็นเฟรม
        bool NoNum => scriptVersion == 0 || scriptVersion >= LiveLink.NONUM_VERSION;   // ยังไม่รู้รุ่น = ไม่แตะ Num Lock ไว้ก่อน

        void ApplyState()
        {
            if (Native)
            {
                if (scroll) { KeyHook.Tap(VK_SCROLL, 0x46, false); scroll = false; lastScrollTap = DateTime.Now; }
                engine.SetGun(gun);
                engine.SetActive(on && !paused);
                LiveDirty();
                return;
            }
            if (NoNum)
            {
                bool want = on && !paused;
                if (want != scroll) { KeyHook.Tap(VK_SCROLL, 0x46, false); scroll = want; lastScrollTap = DateTime.Now; }
                if (on) SyncGun();
                return;
            }
            int st = on && !paused ? gun : 0;     // หยุดชั่วคราว = ส่ง "ปิด" ให้ G Hub แต่ในโปรแกรมยังเปิดอยู่
            bool wantScroll = st == 1 || st == 3;
            bool wantNum = st == 2 || st == 3;
            if (wantScroll && !scroll) { KeyHook.Tap(VK_SCROLL, 0x46, false); scroll = true; }
            if (wantNum && !num) { KeyHook.Tap(VK_NUM, 0x45, false); num = true; }
            if (!wantScroll && scroll) { KeyHook.Tap(VK_SCROLL, 0x46, false); scroll = false; }
            if (!wantNum && num) { KeyHook.Tap(VK_NUM, 0x45, false); num = false; }
        }

        // บอกสคริปต์ว่าใช้ปืนไหน (ส่งเฉพาะตอนเปลี่ยน กดรัวได้ ส่งตัวล่าสุด)
        int gunSent;                 // ปืนที่สคริปต์รู้แล้ว (0 = ไม่รู้ เช่นหลัง G Hub เปิดใหม่)
        bool gunSending;

        async void SyncGun()
        {
            if (Native || gunSending || gun == gunSent) return;
            gunSending = true;
            try
            {
                while (gun != gunSent)
                {
                    for (int i = 0; i < 100 && (liveBusy || setupBusy); i++) await Task.Delay(50);
                    if (liveBusy || setupBusy) return;
                    int g = gun;
                    liveBusy = true;
                    RefreshApplyButton();
                    bool ok;
                    try { ok = await SendFrameAsync(LiveLink.GunFrame(g)); }
                    finally { liveAck = null; liveBusy = false; RefreshApplyButton(); }
                    Log.Info($"live gun {g}: {(ok ? "ok" : "no ack")}");
                    if (!ok) { Flash("G Hub ไม่ตอบรับตอนเปลี่ยนปืน"); return; }
                    gunSent = g;
                }
            }
            finally { gunSending = false; }
        }

        // G Hub เปิดใหม่ = สคริปต์กลับไปปืน 1 → รอโหลดเสร็จแล้วส่งเลขปืนให้ใหม่
        async void GunAfterRestart()
        {
            gunSent = 0;
            multSent[0] = multSent[1] = LiveLink.MultCode(1);     // สคริปต์โหลดใหม่ = ตัวคูณกลับเป็น 1
            PushMults();
            await Task.Delay(10000);
            if (on && NoNum) SyncGun();
        }

        // สคริปต์วนปืนเอง (ปุ่มเมาส์) แล้วกด F13–F15 มาบอก
        void GunFromScript(int g)
        {
            if (vals == null || g > vals.Length) return;
            gunSent = g;
            if (g == gun) return;
            gun = g;
            cfg.Gun = gun;
            cfg.Save();
            UpdateGuns();
            PopOsd();
        }

        // ไฟถูกเปลี่ยนจากข้างนอก (ผู้ใช้กด Scroll Lock / สคริปต์รุ่นเก่าวนปืนด้วยไฟ)
        void SyncFromLocks()
        {
            if (Native) return;                   // ในตัว: ไฟ Lock ไม่เกี่ยว
            if (paused) return;                   // ไฟดับเพราะหยุดชั่วคราว ไม่ใช่ปิดมาโคร
            if (NoNum)
            {
                if (scroll == on) return;         // ผู้ใช้กด Scroll Lock เอง = เปิด/ปิดมาโคร
                on = scroll;
                if (on) { gunSent = 0; SyncGun(); }
                UpdatePower();
                UpdateGuns();
                Sfx.Play(on ? Sfx.On : Sfx.Off, cfg.Sound);
                PopOsd();
                return;
            }
            int st = DecodeState();
            bool newOn = st > 0;
            if (newOn != on)
            {
                on = newOn;
                UpdatePower();
                UpdateGuns();
                Sfx.Play(on ? Sfx.On : Sfx.Off, cfg.Sound);
                PopOsd();
            }
            if (st > 0 && st != gun)
            {
                gun = st;
                cfg.Gun = gun;
                cfg.Save();
                UpdateGuns();
                PopOsd();                    // เปลี่ยนปืนจากปุ่ม Back = บอกบนป้ายอย่างเดียว
            }
        }

        // ---------------------------------------------------------------- ปุ่มที่ดักได้
        void OnKeyUp(int vk)
        {
            // เลิกสแกนอัตโนมัติตอนกด Tab แล้ว: เดาปืนผิดแล้วเขียนทับค่า base เอง (0 ↔ 13) และการจับภาพจอเกมเสี่ยงโดนแบน
        }

        void OnKey(int vk)
        {
            if (capturing) { CaptureKey(vk); return; }
            if (eye != null && eye.Key(vk)) return;          // จูนด้วยตา: ลูกศรตอบคำถามบนป้าย

            if (lootWin?.Capturing == true || scopeWin?.Capturing == true) return;   // กำลังตั้งปุ่มในหน้าต่างย่อย
            if (cfg.LootKey != 0 && vk == cfg.LootKey) { LootKeyPressed(); return; }
            // ปุ่มในเกม (เลขปืน รีโหลด ย่อ/หมอบ Tab) นับเฉพาะตอนเกมอยู่หน้าจอ — พิมพ์เลขในแอปอื่นแล้วค่าเปลี่ยน = บัค
            bool game = GameInFront();
            // แป้นภาษาไทยในเกม: PUBG อ่านปุ่มตามตัวอักษร → เลข/ตัวอักษร (1 2 3, C, Z, B, I …) ไม่ทำงานในเกม
            //   ถ้าโปรแกรมยังนับ = เปลี่ยนปืน/ท่า/โหมดเองทั้งที่ในเกมไม่เปลี่ยน → ไม่นับปุ่มพวกนี้ตอนหน้าต่างเกมไม่ใช่ภาษาอังกฤษ
            if (game && vk >= 0x30 && vk <= 0x5A && !GameKeyboardEnglish()) game = false;
            if (game) StanceKey(vk);                             // ย่อ/หมอบ: ดูอย่างเดียว ไม่กินปุ่ม
            if (game && (vk == 0x09 || vk == 0x49 || vk == 0x1B))   // Tab / I / Esc
            {
                if (invSession) invCloseKeyAt = DateTime.Now;      // ปิดกระเป๋า
                else if (vk != 0x1B) invKeyAt = DateTime.Now;      // เปิด — นับเฉพาะถ้าเคอร์เซอร์เพิ่งโผล่ตามมา (ล็อบบี้มีเคอร์เซอร์อยู่แล้ว = ไม่นับ)
            }
            if (vk == cfg.HotKey) { ToggleOn(); return; }
            // ในตัว: ไม่มีปุ่มเลือกชุด/จูนในเกม (Back/Forward) — ทุกอย่างออโต้จากหน้ากระเป๋า
            if (cfg.PresetKey != 0 && vk == cfg.PresetKey) { CyclePreset(); return; }
            if (cfg.CycleKey != 0 && vk == cfg.CycleKey) { CycleKeyPressed(); return; }
            if (game)
            {
                if (ScopeKeyPressed(vk)) return;
                if (vk == 0x42 && !CursorShown()) FireModeKey();                         // B = เปลี่ยนโหมดยิง
                if (WeaponSlotOf(vk) is int ws and >= 0) WeaponKey(vk, ws);          // ปุ่มอาวุธตามที่ตั้งในเกม: ปืน 1/2 = นับของช่องนั้นต่อ, ปืนพก/มีด/ระเบิด/เก็บปืน = 0
                if (cfg.ScopeAuto && Array.IndexOf(SlotKeys, vk) is int si and >= 0) { HeldSlotTo(si + 1, "weapon key"); return; }
                if (!cfg.ScopeAuto && cfg.SlotSwitch > 0 && (vk == 0x31 || vk == 0x32) && Keyboard.FocusedElement is not TextBox) { SlotTo(vk - 0x30); return; }
            }

            switch (vk)
            {
                case VK_SCROLL:
                case VK_NUM:
                    lastScrollTap = DateTime.Now;
                    if (vk == VK_SCROLL) scroll = !scroll; else num = !num;
                    lockSettle.Stop();
                    lockSettle.Start();
                    break;

                case 0x81: liveAck?.TrySetResult(true); break;   // F18 = G Hub รับค่าแล้ว
                case 0x7C: case 0x7D: case 0x7E: GunFromScript(vk - 0x7B); break;   // F13–F15 = สคริปต์วนไปปืน 1–3
                case 0x84: Tune("base", +0.5); break;   // F21 จาก G Hub
                case 0x85: Tune("base", -0.5); break;   // F22
                case 0x82: Tune("x", +0.5); break;      // F19
                case 0x83: Tune("x", -0.5); break;      // F20
                case 0x86:                              // F23 = เปลี่ยนกล้อง
                    Flash("เปลี่ยนกล้อง");
                    PopOsd("เปลี่ยนกล้อง");
                    Sfx.Play(Sfx.Scope, cfg.Sound);
                    break;
            }
        }

        // ---------------------------------------------------------------- ค่า base / x

        // G Hub จูนค่าในหน่วยความจำแล้วส่งปุ่มมาบอก → GunBeep ตามค่า โชว์บนการ์ด และเซฟลงไฟล์สคริปต์ให้
        void Tune(string field, double delta)
        {
            Sfx.Play(delta > 0 ? Sfx.Up : Sfx.Down, cfg.Sound);
            string arrow = delta > 0 ? "▲" : "▼";
            if (vals == null || gun > vals.Length)
            {
                Flash($"{arrow}  {field} {(delta > 0 ? "+" : "−")}0.5");
                return;
            }
            var v = vals[gun - 1];
            if (field == "base") v.Base += delta; else v.X += delta;
            if (applied != null && gun <= applied.Length)          // G Hub เปลี่ยนค่านี้ในหน่วยความจำแล้ว
            {
                if (field == "base") applied[gun - 1].Base += delta; else applied[gun - 1].X += delta;
            }
            double now = field == "base" ? v.Base : v.X;
            Flash($"{arrow}  {cfg.Names[gun - 1]}  {field} = {GHub.Fmt(now)}");
            UpdateGuns();
            PopOsd($"{arrow}  {field} = {GHub.Fmt(now)}");
            saveVals.Stop();
            saveVals.Start();
        }

        async void LoadGunValues()
        {
            // ค่าที่ปรับแล้วแต่ยังไม่ได้เซฟ (เช่น G Hub รีสตาร์ทตอนกำลังเลื่อนหลอด) ห้ามหาย → ใส่กลับแล้วส่งเข้า G Hub ใหม่
            var pending = saveVals.IsEnabled && vals != null ? Clone(vals) : null;
            saveVals.Stop();
            await fileWork;
            vals = await Task.Run(GHub.ReadGunValues);
            applied = Clone(vals);                                  // G Hub โหลดค่าจากไฟล์เดียวกันนี้
            appliedSteps = vals?.Select(v => v.Steps?.ToArray()).ToArray();
            liveFailed = false;
            if (pending != null && vals != null)
            {
                for (int i = 0; i < Math.Min(vals.Length, pending.Length); i++)
                {
                    vals[i].Base = pending[i].Base;
                    vals[i].X = pending[i].X;
                }
                saveVals.Start();
                liveTimer.Start();
            }
            PushEngine();
            UpdateGuns();
        }

        // งานเขียนไฟล์สคริปต์เบื้องหลัง: เรียงคิวทีละงาน และตอนปิดโปรแกรมรอให้เขียนเสร็จก่อน
        Task fileWork = Task.CompletedTask;

        void QueueFile(string what, Action write) =>
            fileWork = fileWork.ContinueWith(_ =>
            {
                try { write(); }
                catch (Exception ex) { Log.Error(what, ex); }
            }, TaskScheduler.Default);

        static GHub.GunVals[] Clone(GHub.GunVals[] src) =>
            src?.Select(v => new GHub.GunVals { Base = v.Base, X = v.X }).ToArray();

        double[][] appliedSteps;       // กราฟที่ G Hub ใช้อยู่ในหน่วยความจำ
        int scriptVersion;             // เวอร์ชันสคริปต์ใน G Hub (v6+ รับกราฟแบบ real time)

        bool SegsPending =>
            vals != null && appliedSteps != null &&
            Enumerable.Range(0, Math.Min(vals.Length, appliedSteps.Length)).Any(i =>
                vals[i].Steps != null && appliedSteps[i] != null && !LiveLink.SameGraph(vals[i].Steps, appliedSteps[i]));

        bool HasPending =>
            vals != null && applied != null &&
            (vals.Zip(applied).Any(p => Math.Abs(p.First.Base - p.Second.Base) > 1e-9 || Math.Abs(p.First.X - p.Second.X) > 1e-9)
             || SegsPending);

        // ค้างอยู่แต่ส่งแบบ real time ไม่ได้ → ต้องรีสตาร์ท G Hub
        bool NeedRestart => liveFailed || (SegsPending && scriptVersion < LiveLink.SEGS_VERSION);

        // ---------------------------------------------------------------- หลอดเลื่อน

        void UpdateTunePanel()
        {
            TuneTitle.Text = "ปรับค่า · " + cfg.Names[gun - 1];
            bool have = vals != null && gun <= vals.Length;
            BaseSlider.IsEnabled = XSlider.IsEnabled = have;

            syncingSliders = true;
            if (have)
            {
                var v = vals[gun - 1];
                BaseSlider.Maximum = Math.Max(50, Math.Ceiling(v.Base));     // ตัวส่งรับได้ถึง 127.5
                BaseSlider.Minimum = Math.Min(0, Math.Floor(v.Base));
                double xr = Math.Max(5, Math.Ceiling(Math.Abs(v.X)));   // สมมาตร 0 อยู่กลางหลอดเสมอ
                XSlider.Maximum = xr;
                XSlider.Minimum = -xr;
                BaseSlider.Value = v.Base;
                XSlider.Value = v.X;
                BaseValue.Text = GHub.Fmt(v.Base);
                XValue.Text = XText(v.X);
            }
            else
            {
                BaseValue.Text = XValue.Text = "–";
            }
            syncingSliders = false;
            UpdateXBar();
            RefreshApplyButton();
        }

        // x: − = ดึงซ้าย, + = ดึงขวา (มาโครใช้ค่ารวมค่าเดียว)
        static string XText(double x) =>
            Math.Abs(x) < 1e-9 ? "0" : (x < 0 ? "ซ้าย " : "ขวา ") + GHub.Fmt(Math.Abs(x));

        static readonly Brush XRightBrush = new LinearGradientBrush(Color.FromRgb(0x1D, 0x6B, 0xFF), Color.FromRgb(0x5C, 0xE1, 0xFF), 0);
        static readonly Brush XLeftBrush = new LinearGradientBrush(Color.FromRgb(0x5C, 0xE1, 0xFF), Color.FromRgb(0x1D, 0x6B, 0xFF), 0);

        void XBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateXBar();

        // เติมสีจาก 0 (กลาง) ไปถึงค่าปัจจุบัน — ซ้ายหรือขวาเห็นทันที
        void UpdateXBar()
        {
            double w = XSlider.ActualWidth - 16;                    // ช่วงที่จุดกลางปุ่มวิ่งได้ (ปุ่มกว้าง 16)
            double span = XSlider.Maximum - XSlider.Minimum;
            if (w <= 0 || span <= 0) return;
            double Px(double v) => 8 + (Math.Clamp(v, XSlider.Minimum, XSlider.Maximum) - XSlider.Minimum) / span * w;
            double c = Px(0), p = Px(XSlider.Value);
            XMid.Margin = new Thickness(c - 1, 0, 0, 0);
            XFill.Margin = new Thickness(Math.Min(c, p), 0, 0, 0);
            XFill.Width = Math.Abs(p - c);
            XFill.Background = p < c ? XLeftBrush : XRightBrush;
        }

        void RefreshApplyButton()
        {
            bool have = vals != null && gun <= vals.Length;
            bool pending = HasPending;
            ApplyBtn.IsEnabled = pending && !setupBusy && !liveBusy;
            if (Native)
            {
                ApplyBtn.Content = pending ? "ใช้ค่านี้" : have ? "✓  ใช้ค่านี้อยู่" : "ยังไม่มีค่าปืน";
                if (pending) ApplyBtn.BorderBrush = accent; else ApplyBtn.ClearValue(BorderBrushProperty);
                return;
            }
            ApplyBtn.Content = setupBusy ? "กำลังรีสตาร์ท G Hub..."
                             : liveBusy ? "กำลังส่งค่าเข้า G Hub..."
                             : pending && NeedRestart ? "ใช้ค่านี้  (รีสตาร์ท G Hub ~10 วิ)"
                             : pending ? "ส่งค่าเข้า G Hub"
                             : have ? "✓  G Hub ใช้ค่านี้อยู่" : "ยังไม่พบสคริปต์ใน G Hub";
            if (pending) ApplyBtn.BorderBrush = accent; else ApplyBtn.ClearValue(BorderBrushProperty);
        }

        void UpdateValueTexts()
        {
            for (int i = 0; i < GUNS; i++)
                valTexts[i].Text = vals != null && i < vals.Length
                    ? $"base {GHub.Fmt(vals[i].Base)} · x {GHub.Fmt(vals[i].X)}"
                    : "base – · x –";
            UpdatePresetButton();
        }

        // ---------------------------------------------------------------- โปรไฟล์ค่าปืน

        Preset currentPreset;
        bool presetLooked;

        void UpdatePresetButton()
        {
            if (!presetLooked)
            {
                presetLooked = true;
                if (!string.IsNullOrEmpty(cfg.Preset))
                    currentPreset = Preset.List().FirstOrDefault(p => p.Name == cfg.Preset);
            }
            if (currentPreset == null)
            {
                PresetBtn.Content = "▾  เลือกโปรไฟล์ค่าปืน";
                PresetBtn.ToolTip = "เลือกโปรไฟล์ค่าปืนที่บันทึกไว้";
                return;
            }
            bool changed = vals != null && !currentPreset.Matches(cfg.Names, vals);
            PresetBtn.Content = "▾  " + currentPreset.Name + (changed ? "  •" : "");
            PresetBtn.ToolTip = changed
                ? "ค่าถูกแก้ไปจากโปรไฟล์นี้แล้ว — กด \"บันทึก\" เพื่อเก็บทับ"
                : "ใช้โปรไฟล์นี้อยู่";
        }

        ContextMenu DarkMenu(FrameworkElement target)
        {
            var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            if (TryFindResource(typeof(ContextMenu)) is Style s) m.Style = s;
            return m;
        }

        MenuItem Item(string header, Action click, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (TryFindResource(typeof(MenuItem)) is Style s) mi.Style = s;
            if (click != null) mi.Click += (o, e) => click();
            return mi;
        }

        Separator Sep()
        {
            var sp = new Separator();
            if (TryFindResource(typeof(Separator)) is Style s) sp.Style = s;
            return sp;
        }

        static string Summary(Preset p) =>
            string.Join("  ·  ", p.Guns.Select(g => $"{GHub.Fmt(g.Base)}/{GHub.Fmt(g.X)}"));

        void Preset_Click(object sender, RoutedEventArgs e)
        {
            var m = DarkMenu(PresetBtn);
            var list = Preset.List();
            if (list.Count == 0)
                m.Items.Add(Item("ยังไม่มีโปรไฟล์ — กด \"บันทึก\" เพื่อสร้าง", null, false));
            foreach (var p in list)
            {
                var pp = p;
                string mark = currentPreset != null && currentPreset.Name == p.Name ? "✓  " : "     ";
                m.Items.Add(Item($"{mark}{p.Name}      ({Summary(p)})", () => LoadPreset(pp)));
            }
            m.IsOpen = true;
        }

        void PresetSave_Click(object sender, RoutedEventArgs e)
        {
            if (vals == null) { Tell("ยังไม่พบสคริปต์ GunBeep ใน G Hub — กด \"ตั้งค่าอัตโนมัติ\" ก่อน"); return; }
            string name = InputDialog.Ask(this, "บันทึกโปรไฟล์", "ชื่อโปรไฟล์ (เช่น เมาส์ใหม่, sens 40)", currentPreset?.Name ?? "");
            if (name == null) return;
            bool exists = File.Exists(Preset.PathFor(name));
            if (exists && currentPreset?.Name != name && !Ask($"มีโปรไฟล์ \"{name}\" อยู่แล้ว จะบันทึกทับไหม?")) return;
            try
            {
                var p = Preset.From(name, cfg.Names, vals);
                p.Save();
                currentPreset = p;
                cfg.Preset = name;
                cfg.Save();
                UpdatePresetButton();
                Flash("บันทึกโปรไฟล์ \"" + name + "\" แล้ว");
                Sfx.Play(Sfx.Pick, cfg.Sound);
            }
            catch (Exception ex) { Tell("บันทึกไม่สำเร็จ: " + ex.Message); }
        }

        void PresetMore_Click(object sender, RoutedEventArgs e)
        {
            var m = DarkMenu(PresetMoreBtn);
            m.Items.Add(Item("นำเข้าจากไฟล์...", ImportPreset));
            m.Items.Add(Item("ส่งออกค่าตอนนี้เป็นไฟล์...", ExportPreset, vals != null));
            m.Items.Add(Sep());
            m.Items.Add(Item(currentPreset != null ? $"ลบโปรไฟล์ \"{currentPreset.Name}\"" : "ลบโปรไฟล์", DeletePreset, currentPreset != null));
            m.Items.Add(Item("เปิดโฟลเดอร์โปรไฟล์", () =>
            {
                Directory.CreateDirectory(Preset.Dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Preset.Dir + "\"");
            }));
            m.IsOpen = true;
        }

        void LoadPreset(Preset p)
        {
            if (vals == null) { Tell("ยังไม่พบสคริปต์ GunBeep ใน G Hub — กด \"ตั้งค่าอัตโนมัติ\" ก่อน"); return; }
            if (setupBusy || liveBusy) { Flash("กำลังส่งค่าเข้า G Hub อยู่ — ลองใหม่อีกครั้ง"); return; }
            if (p.Matches(cfg.Names, vals)) { currentPreset = p; cfg.Preset = p.Name; cfg.Save(); UpdatePresetButton(); return; }
            bool unsaved = currentPreset == null || !currentPreset.Matches(cfg.Names, vals);
            if (unsaved && !Ask($"ค่าปืนตอนนี้ยังไม่ได้บันทึกเป็นโปรไฟล์\nจะโหลด \"{p.Name}\" ทับเลยไหม?")) return;

            // กราฟแรงดีดในโปรไฟล์ต่างจากที่ใช้อยู่: สคริปต์ v6+ ส่งแบบ real time ได้, รุ่นเก่าต้องรีสตาร์ท G Hub
            var segs = PresetSegs(p, out bool segsDiffer);
            bool live = scriptVersion >= LiveLink.SEGS_VERSION;
            bool withSegs = segsDiffer && (live || Ask($"\"{p.Name}\" มีกราฟแรงดีดต่างจากที่ใช้อยู่\n" +
                                              "ต้องรีสตาร์ท G Hub (~10 วิ) จะใช้กราฟด้วยเลยไหม?\n\n(ไม่ = ใช้แค่ base / x)"));
            ApplyPreset(p, withSegs ? segs : null, null);
        }

        // กราฟของแต่ละปืนหลังโหลดโปรไฟล์ (ปืนที่ต่างจากตอนนี้ = ปัดเป็นแท่งแบบที่ส่งเข้า G Hub)
        double[][] PresetSegs(Preset p, out bool differ)
        {
            var segs = vals.Select(v => v.Steps).ToArray();
            differ = false;
            for (int i = 0; i < Math.Min(p.Guns.Count, vals.Length); i++)
            {
                var st = p.Guns[i].StepArray();
                if (st != null && !LiveLink.SameGraph(st, vals[i].Steps)) { segs[i] = LiveLink.Fit(st); differ = true; }
            }
            return segs;
        }

        // ใส่ค่าโปรไฟล์แล้วส่งเข้า G Hub แบบ real time (กราฟด้วยถ้าสคริปต์ v6+, ไม่งั้นรีสตาร์ท G Hub)
        void ApplyPreset(Preset p, double[][] segs, string osdNote)
        {
            for (int i = 0; i < Math.Min(p.Guns.Count, vals.Length); i++)
            {
                vals[i].Base = p.Guns[i].Base;
                vals[i].X = p.Guns[i].X;
                if (segs != null && segs[i] != null) vals[i].Steps = segs[i];
                if (p.Guns[i].Name.Length > 0 && i < cfg.Names.Length) cfg.Names[i] = p.Guns[i].Name;
            }
            currentPreset = p;
            cfg.Preset = p.Name;
            cfg.Save();
            UpdateGuns();
            Flash("โหลดโปรไฟล์ \"" + p.Name + "\" แล้ว");
            Sfx.Play(Sfx.Pick, cfg.Sound);
            PopOsd("โปรไฟล์: " + p.Name + (osdNote ?? ""));
            if (segs != null && scriptVersion < LiveLink.SEGS_VERSION)
            {
                _ = ApplyRestartAsync(segs);
                return;
            }
            saveVals.Stop();
            saveVals.Start();
            liveFailed = false;              // ลองส่งแบบ real time ใหม่ทุกครั้งที่โหลดโปรไฟล์
            liveTimer.Stop();                // ส่งเข้า G Hub เหมือนเลื่อนหลอดเอง
            liveTimer.Start();
        }

        // ปุ่มลัดในเกม: วนโปรไฟล์ถัดไปทันที ไม่มีหน้าต่างถาม (เด้งกล่องกลางเกมไม่ได้)
        //   ค่าตอนนี้ยังไม่ได้บันทึก → เก็บเป็นโปรไฟล์ "ก่อนสลับ (อัตโนมัติ)" ให้ก่อน จะได้ไม่หาย
        //   กราฟต่าง → ใช้แค่ base/x (รีสตาร์ท G Hub กลางเกมไม่ได้) แล้วบอกบนป้าย
        const string AutoPresetName = "ก่อนสลับ (อัตโนมัติ)";

        void CyclePreset()
        {
            if (vals == null) { PopOsd("ยังไม่พบสคริปต์ใน G Hub"); return; }
            if (setupBusy) return;
            var list = Preset.List().Where(x => x.Name != AutoPresetName).ToList();
            if (list.Count == 0)
            {
                Flash("ยังไม่มีโปรไฟล์ — กด \"บันทึก\" ในโปรแกรมก่อน");
                PopOsd("ยังไม่มีโปรไฟล์");
                Sfx.Play(Sfx.Down, cfg.Sound);
                return;
            }
            if (currentPreset == null || !currentPreset.Matches(cfg.Names, vals))
            {
                try { Preset.From(AutoPresetName, cfg.Names, vals).Save(); } catch { }
            }
            int cur = currentPreset == null ? -1 : list.FindIndex(x => x.Name == currentPreset.Name);
            var p = list[(cur + 1) % list.Count];

            var segs = PresetSegs(p, out bool segsDiffer);
            bool live = scriptVersion >= LiveLink.SEGS_VERSION;
            ApplyPreset(p, segsDiffer && live ? segs : null,
                        $"  ({list.IndexOf(p) + 1}/{list.Count})" + (segsDiffer && !live ? "\nกราฟยังไม่ใช้ — เลือกในหน้าต่าง" : ""));
        }

        // ---------------------------------------------------------------- กราฟแรงดีด (segs)

        async void Segs_Click(object sender, RoutedEventArgs e)
        {
            if (vals == null) { Tell("ยังไม่พบสคริปต์ GunBeep ใน G Hub — กด \"ตั้งค่าอัตโนมัติ\" ก่อน"); return; }
            if (setupBusy || liveBusy) return;
            bool live = Native || scriptVersion >= LiveLink.SEGS_VERSION;
            var res = SegsEditor.Edit(this, cfg.Names.Take(vals.Length).ToArray(),
                                      vals.Select(v => v.Steps).ToArray(), vals.Select(v => v.Base).ToArray(), gun, live);
            if (res == null) return;
            // ปืนที่แก้ = ปัดเป็นแท่งละ 100 ms ค่าละเอียด 0.25 (แบบเดียวกับที่ส่งเข้า G Hub)
            for (int i = 0; i < vals.Length; i++)
                if (!GHub.SameSteps(res[i], vals[i].Steps)) vals[i].Steps = Native ? res[i] : LiveLink.Fit(res[i]);
            UpdatePresetButton();
            RefreshApplyButton();
            if (live)
            {
                liveFailed = false;
                if (await ApplyLiveAsync() && !HasPending) return;
                if (!Ask("ส่งกราฟเข้า G Hub แบบ real time ไม่สำเร็จ\nจะใช้วิธีรีสตาร์ท G Hub (~10 วินาที) แทนไหม?")) return;
            }
            await ApplyRestartAsync();
        }

        // ตารางรายนัด: ค่าละเอียดทุกนัด ส่งแบบ real time ไม่ได้ (เฟรมกราฟปัดเป็นแท่ง 100 ms) → เขียนไฟล์ + รีสตาร์ท G Hub
        async void Shots_Click(object sender, RoutedEventArgs e)
        {
            if (vals == null) { Tell("ยังไม่พบสคริปต์ GunBeep ใน G Hub — กด \"ตั้งค่าอัตโนมัติ\" ก่อน"); return; }
            if (setupBusy || liveBusy) return;
            var d = ShotEditor.Edit(this, cfg.Names.Take(vals.Length).ToArray(), vals.Select(v => v.Steps).ToArray(),
                                    vals.Select(v => v.Base).ToArray(), cfg.ShotMs, cfg.ShotCount, gun);
            if (d == null) return;
            cfg.ShotMs = d.ResultMs.ToArray();
            cfg.ShotCount = d.ResultCount.ToArray();
            cfg.Save();
            var res = d.ResultSteps;
            if (res.All(s => s == null)) return;
            for (int i = 0; i < vals.Length; i++)
                if (res[i] != null) vals[i].Steps = res[i];
            var all = vals.Select(v => (v.Steps ?? GHub.DefaultSteps()).ToArray()).ToArray();
            UpdatePresetButton();
            Log.Info("shot table: " + string.Join(" | ", res.Select((s, i) => s == null ? "-" : $"set {i + 1} {cfg.ShotCount[i]} shots @ {cfg.ShotMs[i]} ms")));
            await ApplyRestartAsync(all);
        }

        void ImportPreset()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "นำเข้าโปรไฟล์ค่าปืน", Filter = "โปรไฟล์ GunBeep (*.json)|*.json" };
            if (dlg.ShowDialog(this) != true) return;
            Preset p;
            try { p = Preset.Load(dlg.FileName); }
            catch (Exception ex) { Tell("นำเข้าไม่ได้: " + ex.Message); return; }
            if (File.Exists(Preset.PathFor(p.Name)) && !Ask($"มีโปรไฟล์ \"{p.Name}\" อยู่แล้ว จะแทนที่ไหม?")) return;
            try { p.Save(); }
            catch (Exception ex) { Tell("บันทึกไม่สำเร็จ: " + ex.Message); return; }
            Flash("นำเข้าโปรไฟล์ \"" + p.Name + "\" แล้ว");
            if (vals != null && Ask($"นำเข้า \"{p.Name}\" แล้ว\n({Summary(p)})\n\nจะใช้โปรไฟล์นี้เลยไหม?"))
                ApplyPresetValues(p);
        }

        void ApplyPresetValues(Preset p)
        {
            // ถามผู้ใช้ไปแล้ว: ทำให้ค่าตอนนี้ "ตรงกับโปรไฟล์" ชั่วคราว LoadPreset จะได้ไม่ถามซ้ำ
            currentPreset = Preset.From("", cfg.Names, vals);
            LoadPreset(p);
        }

        void ExportPreset()
        {
            if (vals == null) return;
            string name = currentPreset?.Name ?? ("GunBeep " + DateTime.Now.ToString("yyyy-MM-dd"));
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "ส่งออกโปรไฟล์ค่าปืน", Filter = "โปรไฟล์ GunBeep (*.json)|*.json",
                FileName = Path.GetFileName(Preset.PathFor(name))
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, Preset.From(name, cfg.Names, vals).ToJson());
                Flash("ส่งออกแล้ว: " + Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex) { Tell("ส่งออกไม่สำเร็จ: " + ex.Message); }
        }

        void DeletePreset()
        {
            if (currentPreset == null || !Ask($"ลบโปรไฟล์ \"{currentPreset.Name}\"?\n(ค่าปืนที่ใช้อยู่ตอนนี้ไม่เปลี่ยน)")) return;
            try { Preset.Delete(currentPreset.Name); }
            catch (Exception ex) { Tell("ลบไม่สำเร็จ: " + ex.Message); return; }
            Flash("ลบโปรไฟล์ \"" + currentPreset.Name + "\" แล้ว");
            currentPreset = null;
            cfg.Preset = null;
            cfg.Save();
            UpdatePresetButton();
        }

        void SliderMoved(string field, double value)
        {
            if (syncingSliders || vals == null || gun > vals.Length) return;
            value = Math.Round(value * 2) / 2;                       // ทีละ 0.5
            var v = vals[gun - 1];
            if (field == "base") { if (v.Base == value) return; v.Base = value; BaseValue.Text = GHub.Fmt(value); }
            else { if (v.X == value) return; v.X = value; XValue.Text = XText(value); }
            UpdateValueTexts();
            RefreshApplyButton();
            saveVals.Stop();
            saveVals.Start();
            liveTimer.Stop();                // หยุดเลื่อนครู่หนึ่งแล้วส่งเข้า G Hub ให้เอง
            liveTimer.Start();
        }

        // ---------------------------------------------------------------- ส่งค่าเข้า G Hub แบบ real time

        static GHub.GunVals Snap(GHub.GunVals v) =>
            new GHub.GunVals { Base = Math.Round(v.Base * 2) / 2, X = Math.Round(v.X * 2) / 2 };

        static bool Same(GHub.GunVals a, GHub.GunVals b) =>
            Math.Abs(a.Base - b.Base) < 1e-9 && Math.Abs(a.X - b.X) < 1e-9;

        async Task<bool> ApplyLiveAsync()
        {
            if (liveBusy) { liveAgain = true; return true; }
            if (setupBusy || vals == null || applied == null) return false;
            if (Native)
            {
                // ในตัว: ใช้ทันที ไม่ต้องส่งเฟรม — กราฟที่เปลี่ยนเขียนลงไฟล์ด้วย
                bool segsChanged = appliedSteps == null || vals.Where((v, i) => i >= appliedSteps.Length || !GHub.SameSteps(v.Steps, appliedSteps[i])).Any();
                applied = Clone(vals);
                appliedSteps = vals.Select(v => v.Steps?.ToArray()).ToArray();
                liveFailed = false;
                if (segsChanged && vals.All(v => v.Steps != null))
                {
                    var all = vals.Select(v => v.Steps.ToArray()).ToArray();
                    QueueFile("save segs", () => GHub.WriteSegs(all));
                }
                PushEngine();
                RefreshApplyButton();
                return true;
            }
            liveBusy = true;
            RefreshApplyButton();
            bool ok = true, segsSent = false;
            try
            {
                bool sent = false;
                do
                {
                    liveAgain = false;
                    for (int i = 0; i < Math.Min(vals.Length, applied.Length) && ok; i++)
                    {
                        var want = Snap(vals[i]);
                        if (!Same(want, applied[i]))
                        {
                            // สคริปต์เริ่มเฟรมใหม่เมื่อ Right Ctrl เงียบเกิน 150 ms → เว้นก่อนส่งเฟรมถัดไป
                            if (sent) await Task.Delay(LiveLink.FRAME_GAP_MS);
                            sent = true;
                            if (!await SendFrameAsync(LiveLink.Frame(i + 1, want.Base, want.X))) { ok = false; break; }
                            applied[i] = want;
                        }

                        // กราฟ (สคริปต์ v6+)
                        var st = vals[i].Steps;
                        if (scriptVersion < LiveLink.SEGS_VERSION || st == null || appliedSteps == null
                            || i >= appliedSteps.Length || appliedSteps[i] == null || LiveLink.SameGraph(st, appliedSteps[i])) continue;
                        st = LiveLink.Fit(st);                     // ย่อให้ส่งได้ (ค่าในโปรแกรม/ไฟล์ = ที่ส่งจริง)
                        vals[i].Steps = st;
                        var bits = LiveLink.SegsFrame(i + 1, st);
                        if (bits == null) { ok = false; Log.Error($"live: graph of gun {i + 1} cannot be encoded"); break; }
                        st = st.ToArray();
                        int secs = (int)Math.Ceiling(LiveLink.Millis(bits) / 1000.0);
                        Flash($"กำลังส่งกราฟ {cfg.Names[i]} เข้า G Hub (~{secs} วิ)...");
                        PopOsd($"กำลังส่งกราฟ {cfg.Names[i]} (~{secs} วิ)");
                        if (sent) await Task.Delay(LiveLink.FRAME_GAP_MS);
                        sent = true;
                        if (!await SendFrameAsync(bits)) { ok = false; break; }
                        appliedSteps[i] = st;
                        segsSent = true;
                    }
                } while (ok && liveAgain);
            }
            finally
            {
                liveAck = null;
                liveBusy = false;
            }
            // กราฟที่ส่งแล้วเขียนลงไฟล์สคริปต์ด้วย (เปิด G Hub ใหม่ครั้งหน้าจะได้ค่าเดียวกัน)
            if (segsSent && vals.All(v => v.Steps != null))
            {
                var all = vals.Select(v => v.Steps.ToArray()).ToArray();
                QueueFile("save segs", () => GHub.WriteSegs(all));
            }
            liveFailed = !ok;
            if (ok) Log.Info("live: " + string.Join(" | ", applied.Select(g => $"base {GHub.Fmt(g.Base)} x {GHub.Fmt(g.X)}")) + (segsSent ? " + segs" : ""));
            else Log.Error("live: G Hub did not ack");
            Flash(ok ? "ส่งค่าเข้า G Hub แล้ว (ไม่ต้องรีสตาร์ท)" : "G Hub ไม่ตอบรับ — สคริปต์อาจยังเป็นรุ่นเก่า");
            if (ok) Sfx.Play(Sfx.Pick, cfg.Sound);
            PopOsd(ok ? "✓  ส่งค่าเข้า G Hub แล้ว" : "✗  G Hub ไม่ตอบรับ");
            RefreshApplyButton();
            return ok;
        }

        // ส่งหนึ่งเฟรมแล้วรอ F18 (ไม่ได้รับ → ลองใหม่อีกครั้ง เผื่อสคริปต์ไม่ว่างตอนนั้น)
        async Task<bool> SendFrameAsync(int[] bits)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) await Task.Delay(LiveLink.FRAME_GAP_MS);
                liveAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.Run(() => LiveLink.Send(bits));
                if (await Task.WhenAny(liveAck.Task, Task.Delay(1000)) == liveAck.Task) return true;
            }
            return false;
        }

        async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (!HasPending) return;
            if (!liveFailed && await ApplyLiveAsync() && !HasPending) return;
            string why = liveFailed
                ? "ส่งค่าเข้า G Hub แบบ real time ไม่สำเร็จ\n(สคริปต์ใน G Hub อาจยังเป็นรุ่นเก่า — กด \"ตั้งค่าอัตโนมัติ\" เพื่ออัปเดต)"
                : $"สคริปต์ใน G Hub ยังเป็น v{scriptVersion} ส่งกราฟแบบ real time ไม่ได้\n(กด \"ตั้งค่าอัตโนมัติ\" เพื่ออัปเดตเป็น v{LiveLink.SEGS_VERSION})";
            if (Ask(why + "\n\nจะใช้วิธีรีสตาร์ท G Hub (~10 วินาที) แทนไหม?"))
                await ApplyRestartAsync();
        }

        void Step_Click(object sender, RoutedEventArgs e)
        {
            var tag = (string)((FrameworkElement)sender).Tag;      // "base+", "x-", ...
            var slider = tag.StartsWith("base") ? BaseSlider : XSlider;
            double d = tag.EndsWith("+") ? 0.5 : -0.5;
            if (!slider.IsEnabled) return;
            double target = slider.Value + d;
            if (slider == XSlider && Math.Abs(target) > XSlider.Maximum) { XSlider.Maximum = Math.Abs(target); XSlider.Minimum = -Math.Abs(target); }
            if (target > slider.Maximum) slider.Maximum = target;
            if (target < slider.Minimum) slider.Minimum = target;
            slider.Value = target;
        }

        // ทางสำรอง: เขียนค่าลงไฟล์แล้วรีสตาร์ท G Hub ให้โหลดใหม่
        async Task ApplyRestartAsync(double[][] segs = null)
        {
            if (setupBusy || liveBusy || vals == null || (!HasPending && segs == null)) return;
            if (segs == null && SegsPending && vals.All(v => v.Steps != null))
                segs = vals.Select(v => v.Steps.ToArray()).ToArray();
            if (Native) { SaveNative(segs); return; }
            setupBusy = true;
            UpdateTunePanel();
            var copy = Clone(vals);
            bool stopped = false;
            try
            {
                saveVals.Stop();
                Flash("กำลังรีสตาร์ท G Hub เพื่อใช้ค่าใหม่...");
                stopped = true;
                await fileWork;                       // งานเขียนไฟล์เก่าที่ค้างคิวห้ามมาเขียนทับทีหลัง
                await Task.Run(async () =>
                {
                    if (segs != null) GHub.WriteSegs(segs);
                    GHub.WriteGunValues(copy);
                    await GHub.StopAsync();
                    GHub.StartBackground();
                    await GHub.WaitAgentAsync(25);
                });
                stopped = false;
                agentPid = GHub.AgentPid();
                GunAfterRestart();
                applied = Clone(copy);
                if (segs != null) appliedSteps = segs.Select(s => s?.ToArray()).ToArray();
                liveFailed = false;
                Flash("ใช้ค่าใหม่แล้ว");
                Sfx.Play(Sfx.Ready, cfg.Sound);
            }
            catch (Exception ex)
            {
                Tell("ใช้ค่าใหม่ไม่สำเร็จ: " + ex.Message);
                if (segs != null) LoadGunValues();     // กราฟอาจไม่ได้เขียนลงไฟล์ → อ่านของจริงกลับมา
            }
            finally
            {
                if (stopped && !GHub.IsRunning) try { GHub.StartBackground(); } catch { }
                setupBusy = false;
                UpdateTunePanel();
            }
        }

        void FlushGunValues()
        {
            saveVals.Stop();
            if (vals != null)
            {
                var copy = vals.Select(v => new GHub.GunVals { Base = v.Base, X = v.X }).ToArray();
                QueueFile("save guns", () => GHub.WriteGunValues(copy));
            }
            if (!fileWork.Wait(4000)) Log.Error("file writes still running at exit");
        }

        void ToggleOn()
        {
            on = !on;
            if (on) { gunSent = 0; PushMults(); }   // เปิดใหม่ = ส่งเลขปืนให้สคริปต์ทุกครั้ง (G Hub อาจเพิ่งเปิดใหม่) + ตัวคูณ
            ApplyState();
            UpdatePower();
            UpdateGuns();
            Sfx.Play(on ? Sfx.On : Sfx.Off, cfg.Sound);
            PopOsd();
        }

        void SelectGun(int g)
        {
            bool changed = g != gun;
            if (!on)
            {
                // ตอนปิด: แค่เลือกปืนที่จะปรับค่า / ใช้ตอนเปิดครั้งหน้า (ไม่ส่งอะไรให้ G Hub)
                gun = g;
                cfg.Gun = gun;
                cfg.Save();
                UpdateGuns();
                return;
            }
            gun = g;
            cfg.Gun = gun;
            cfg.Save();
            ApplyState();
            UpdateGuns();
            if (changed) PopOsd();          // เปลี่ยนปืน = บอกบนป้ายอย่างเดียว ไม่มีเสียง
        }

        // ---------------------------------------------------------------- ปุ่มลัด

        static bool HotkeyAllowed(int vk) =>
            !(vk == VK_NUM || vk == VK_SCROLL                  // ใช้ส่งสถานะให้ G Hub
              || (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C
              || (vk >= 0x7C && vk <= 0x87)                    // F13-F24 ที่ G Hub ส่งมา
              || (vk < 0x08 && vk != 4 && vk != 5 && vk != 6));   // 4-6 = ปุ่มเมาส์กลาง/Back/Forward ใช้ได้

        internal static string KeyName(int vk)
        {
            string s = KeyInterop.KeyFromVirtualKey(vk).ToString();
            if (vk == 4) return "เมาส์ปุ่มกลาง";
            if (vk == 5) return "เมาส์ Back";
            if (vk == 6) return "เมาส์ Forward";
            if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s.Substring(1);
            if (s.StartsWith("NumPad")) return "Num " + s.Substring(6);
            switch (s)
            {
                case "Oem3": return "`";
                case "OemMinus": return "-";
                case "OemPlus": return "=";
                case "Oem5": return "\\";
                case "OemOpenBrackets": return "[";
                case "Oem6": return "]";
                case "Oem1": return ";";
                case "OemQuotes": return "'";
                case "OemComma": return ",";
                case "OemPeriod": return ".";
                case "OemQuestion": return "/";
                case "Capital": return "Caps Lock";
                case "Return": return "Enter";
                case "Back": return "Backspace";
                case "Prior": return "Page Up";
                case "Next": return "Page Down";
                case "Snapshot": return "Print Screen";
                default: return s;
            }
        }

        // ---------------------------------------------------------------- ปุ่มวนปืน / จูนในเกม
        //   ปุ่มเมาส์ 3/4/5 = สคริปต์ใน G Hub จัดการ (เร็วสุด) — เปลี่ยนแบบ real time ด้วยเฟรมตั้งค่า (สคริปต์ v7+)
        //   ปุ่มคีย์บอร์ด = GunBeep จัดการเอง: วนปืนด้วยไฟ Lock, จูน base/x แล้วส่งเข้า G Hub แบบ real time

        static string MouseName(int b) => b switch { 3 => "เมาส์ปุ่มกลาง", 4 => "เมาส์ Back", 5 => "เมาส์ Forward", _ => "ไม่ใช้" };

        void UpdateCycleText() =>
            CycleBtn.Content = "ในเกม: " + (cfg.CycleKey != 0 ? KeyName(cfg.CycleKey) : MouseName(cfg.CycleMouse)) + "  ▾";

        void Cycle_Click(object sender, RoutedEventArgs e)
        {
            var m = DarkMenu(CycleBtn);
            foreach (int b in new[] { 4, 5, 3 })
                m.Items.Add(Item((cfg.CycleKey == 0 && cfg.CycleMouse == b ? "✓  " : "     ") + MouseName(b), () => SetCycle(b, 0)));
            m.Items.Add(Item((cfg.CycleKey != 0 ? "✓  " + KeyName(cfg.CycleKey) + " (เปลี่ยน...)" : "     ปุ่มคีย์บอร์ด..."), () => BeginCapture(2)));
            m.Items.Add(Item((cfg.CycleKey == 0 && cfg.CycleMouse == 0 ? "✓  " : "     ") + "ไม่ใช้", () => SetCycle(0, 0)));
            m.Items.Add(Sep());
            m.Items.Add(Item("กดเปล่า = วนปืน · Ctrl/Alt = base ±0.5 · Shift+Ctrl/Alt = ซ้าย/ขวา", null, false));
            m.IsOpen = true;
        }

        void SetCycle(int mouse, int key)
        {
            cfg.CycleMouse = mouse;
            cfg.CycleKey = key;
            cfg.Save();
            UpdateCycleText();
            QueueFile("save cycle button", () => GHub.WriteCycleButton(mouse));
            Flash("ปุ่มในเกม = " + (key != 0 ? KeyName(key) : MouseName(mouse)));
            Sfx.Play(Sfx.Pick, cfg.Sound);
            _ = SendCycleAsync();
        }

        // บอกสคริปต์ใน G Hub ว่าใช้ปุ่มเมาส์ไหน (คีย์บอร์ด = 0 ปิดฝั่ง G Hub)
        async Task SendCycleAsync()
        {
            if (Native) return;                       // ในตัว: GunBeep ดูปุ่มเมาส์เอง
            if (scriptVersion < LiveLink.CFG_VERSION)
            {
                Flash("สคริปต์ใน G Hub ยังเป็นรุ่นเก่า — กด \"ตั้งค่าอัตโนมัติ\" เพื่อใช้ปุ่มใหม่");
                return;
            }
            for (int i = 0; i < 100 && (liveBusy || setupBusy); i++) await Task.Delay(200);
            if (liveBusy || setupBusy) return;
            liveBusy = true;
            RefreshApplyButton();
            bool ok;
            try { ok = await SendFrameAsync(LiveLink.CfgFrame(0, cfg.CycleMouse)); }
            finally { liveAck = null; liveBusy = false; RefreshApplyButton(); }
            Log.Info($"live cycle button {cfg.CycleMouse}: {(ok ? "ok" : "no ack")}");
            Flash(ok ? "G Hub ใช้ปุ่มใหม่แล้ว" : "G Hub ไม่ตอบรับ — ปุ่มใหม่จะใช้ได้หลัง G Hub เปิดใหม่");
        }

        void UpdateAdsModeButton()
        {
            AdsAllRb.IsChecked = !cfg.RequireAds;
            AdsHoldRb.IsChecked = cfg.RequireAds;
        }

        void AdsRb_Checked(object sender, RoutedEventArgs e)
        {
            bool req = sender == AdsHoldRb;
            if (req == cfg.RequireAds) return;          // ตั้งค่าตอนเปิดโปรแกรม ไม่ใช่ผู้ใช้กด
            cfg.RequireAds = req;
            cfg.Save();
            Flash(cfg.RequireAds ? "🎯 ดึงเฉพาะตอนกดคลิกขวาค้าง (เล็ง ADS)" : "🔫 ดึงทุกครั้งที่ยิง (รวมยิงสะโพก)");
            Sfx.Play(Sfx.Pick, cfg.Sound);
            try { GHub.WriteRequireAds(cfg.RequireAds); } catch { }
            _ = SendAdsModeAsync();
        }

        void UpdateMagMode()
        {
            MagOnRb.IsChecked = cfg.MagMode != 0;
            MagOffRb.IsChecked = cfg.MagMode == 0;
            bool shots = cfg.MagMode != 0;
            ShotsRow.Opacity = shots ? 1 : 0.4;
            ShotsRow.IsEnabled = shots;
        }

        void MagRb_Checked(object sender, RoutedEventArgs e)
        {
            int mode = sender == MagOnRb ? 1 : 0;
            if (mode == cfg.MagMode) return;            // ตั้งค่าตอนเปิดโปรแกรม ไม่ใช่ผู้ใช้กด
            cfg.MagMode = mode;
            cfg.Save();
            UpdateMagMode();
            Array.Clear(magFiredBy);
            Array.Clear(magEmptyBy);
            fireStart = null;
            Flash(mode == 1 ? "นับกระสุน: ครบจำนวนนัดแล้วหยุดดึงจนกว่าจะกด R" : "ไม่หยุด: ดึงตลอด (เทรนนิ่ง)");
            Sfx.Play(Sfx.Pick, cfg.Sound);
            _ = SendMaxShotsAsync();                    // ไม่หยุด = สคริปต์เลิกหยุดดึงตอนยิงยาวด้วย
        }

        // สคริปต์หยุดดึงเมื่อยิงค้างชุดเดียวเกินจำนวนนี้ (ไม่หยุด = 0)
        int EffectiveMaxShots => cfg.MagMode != 0 ? MagCap(magSlot) + 2 : 0;

        // ---------------------------------------------------------------- หยุดดึงหลังยิงครบ N นัด
        DispatcherTimer shotsSave;

        void UpdateShotsText() => ShotsBox.Text = cfg.MaxShots > 0 ? cfg.MaxShots.ToString() : "∞";

        void ShotsStep_Click(object sender, RoutedEventArgs e) =>
            ShotsSlider.Value = Math.Clamp(ShotsSlider.Value + ((string)((Button)sender).Tag == "+" ? 1 : -1), ShotsSlider.Minimum, ShotsSlider.Maximum);

        void ShotsTyped()
        {
            if (int.TryParse(ShotsBox.Text.Trim(), out int n))
                ShotsSlider.Value = Math.Clamp(n, (int)ShotsSlider.Minimum, (int)ShotsSlider.Maximum);
            UpdateShotsText();                     // พิมพ์ผิด/เกินช่วง = กลับเป็นค่าจริง
        }

        // ---------------------------------------------------------------- SOCD
        SocdWindow socdWin;

        void ApplySocd()
        {
            Socd.Mode = cfg.SocdMode;
            Socd.SetPairs(cfg.SocdPairs);
        }

        void UpdateSocdBtn()
        {
            string pairs = cfg.SocdPairs == null || cfg.SocdPairs.Count == 0 ? "ไม่มีคู่ปุ่ม"
                : string.Join(" ", cfg.SocdPairs.Select(p => KeyName(p[0]) + "⇄" + KeyName(p[1])));
            SocdBtn.Content = cfg.Socd ? "เปิด · " + pairs : "ปิด";
        }

        void Socd_Click(object sender, RoutedEventArgs e)
        {
            if (socdWin != null) { socdWin.Activate(); return; }
            socdWin = new SocdWindow(cfg, KeyName, () => { ApplySocd(); UpdateSocdBtn(); }) { Owner = this };
            socdWin.Closed += (s, a) => socdWin = null;
            socdWin.Show();
        }

        // ---------------------------------------------------------------- เก็บของเร็ว
        LootWindow lootWin;

        void UpdateLootBtn() => UpdateLootPage();

        void Loot_Click(object sender, RoutedEventArgs e)
        {
            if (lootWin != null) { lootWin.Activate(); return; }
            lootWin = new LootWindow(cfg, KeyName, vk => vk != cfg.LootKey && KeyInUse(vk), UpdateLootBtn) { Owner = this };
            lootWin.Closed += (s, a) => lootWin = null;
            lootWin.Show();
        }

        // ปุ่มนี้ GunBeep ใช้ทำอย่างอื่นอยู่แล้วหรือยัง
        bool KeyInUse(int vk) =>
            new[] { cfg.HotKey, cfg.PresetKey, cfg.CycleKey, cfg.LootKey }.Concat(ScopeKeys).Contains(vk)
            || (vk >= 4 && vk <= 6 && cfg.CycleKey == 0 && cfg.CycleMouse == vk - 1);

        // เก็บของเร็ว: เปิดกระเป๋าอยู่ = เก็บเลย; กำลังเล่น (วิ่งอยู่) = กด Tab เปิดให้ → เก็บ → กด Tab ปิดให้
        async void LootKeyPressed()
        {
            if (!GameInFront()) return;
            var hwnd = GetForegroundWindow();
            bool StillOk() => GetForegroundWindow() == hwnd && CursorShown();
            if (Loot.Busy) { Loot.Run(cfg, StillOk, null); Log.Info("loot: stopped by key"); return; }
            bool opened = false;
            if (!CursorShown())
            {
                invKeyAt = DateTime.Now;                          // ให้ระบบตรวจปืนนับว่าเปิดกระเป๋าด้วย Tab
                KeyHook.Tap(0x09, 0x0F, false);
                for (int i = 0; i < 20 && !CursorShown(); i++) await Task.Delay(30);
                if (!CursorShown()) { Log.Info("loot: inventory did not open"); return; }
                await Task.Delay(120);                            // รอหน้ากระเป๋าวาดเสร็จ
                opened = true;
            }
            Loot.Run(cfg, StillOk, moved => Dispatcher.BeginInvoke(new Action(async () =>
            {
                Log.Info($"loot: {moved} slot(s)");
                if (!opened) return;
                await Task.Delay(80);
                if (GetForegroundWindow() == hwnd && CursorShown())
                {
                    invCloseKeyAt = DateTime.Now;
                    KeyHook.Tap(0x09, 0x0F, false);                // ปิดกระเป๋าที่เปิดให้
                }
            })));
        }

        void ShotsMoved(int n)
        {
            if (n == cfg.MaxShots) return;
            cfg.MaxShots = n;
            cfg.Save();
            UpdateShotsText();
            // เลื่อนไปมาได้ ส่งครั้งเดียวหลังหยุดเลื่อน
            if (shotsSave == null)
            {
                shotsSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                shotsSave.Tick += (s, a) => { shotsSave.Stop(); _ = SendMaxShotsAsync(); };
            }
            shotsSave.Stop();
            shotsSave.Start();
        }

        async Task SendMaxShotsAsync()
        {
            int max = EffectiveMaxShots;
            try { GHub.WriteMaxShots(max); } catch { }
            if (Native) { engine.MaxShots = max; Flash($"หยุดดึงหลังยิง {(max > 0 ? max + " นัด" : "ไม่จำกัด")}"); return; }
            if (scriptVersion < LiveLink.SHOTS_VERSION)
            {
                Flash("สคริปต์ใน G Hub ยังเป็นรุ่นเก่า — กด \"ตั้งค่าอัตโนมัติ\" เพื่อใช้จำนวนนัด");
                return;
            }
            for (int i = 0; i < 100 && (liveBusy || setupBusy); i++) await Task.Delay(200);
            if (liveBusy || setupBusy) return;
            liveBusy = true;
            RefreshApplyButton();
            bool ok;
            try { ok = await SendFrameAsync(LiveLink.CfgFrame(2, max)); }
            finally { liveAck = null; liveBusy = false; RefreshApplyButton(); }
            Log.Info($"live max shots {max}: {(ok ? "ok" : "no ack")}");
            Flash(ok ? $"หยุดดึงหลังยิง {(max > 0 ? max + " นัด" : "ไม่จำกัด")}" : "G Hub ไม่ตอบรับ — จะใช้ได้หลัง G Hub เปิดใหม่");
        }

        async Task SendAdsModeAsync()
        {
            if (Native) { engine.RequireAds = cfg.RequireAds; return; }
            if (scriptVersion < LiveLink.ADS_VERSION)
            {
                Flash("สคริปต์ใน G Hub ยังเป็นรุ่นเก่า — กด \"ตั้งค่าอัตโนมัติ\" เพื่อเปลี่ยนโหมดทันที");
                return;
            }
            for (int i = 0; i < 100 && (liveBusy || setupBusy); i++) await Task.Delay(200);
            if (liveBusy || setupBusy) return;
            liveBusy = true;
            RefreshApplyButton();
            bool ok;
            try { ok = await SendFrameAsync(LiveLink.CfgFrame(1, cfg.RequireAds ? 1 : 0)); }
            finally { liveAck = null; liveBusy = false; RefreshApplyButton(); }
            Log.Info($"live ads mode {cfg.RequireAds}: {(ok ? "ok" : "no ack")}");
        }

        // ปุ่มคีย์บอร์ดในเกม (เฉพาะตอนเปิดมาโคร)
        void CycleKeyPressed()
        {
            if (!on) return;
            bool sh = KeyHook.GetAsyncKeyState(0x10) < 0, ct = KeyHook.GetAsyncKeyState(0x11) < 0, al = KeyHook.GetAsyncKeyState(0x12) < 0;
            if ((ct || al) && vals != null && gun <= vals.Length)
            {
                string field = sh ? "x" : "base";
                double d = ct ? 0.5 : -0.5;
                var v = vals[gun - 1];
                if (field == "base") v.Base = Math.Max(0, v.Base + d); else v.X += d;
                double now = field == "base" ? v.Base : v.X;
                Sfx.Play(d > 0 ? Sfx.Up : Sfx.Down, cfg.Sound);
                string label = field == "base" ? "base = " + GHub.Fmt(now) : "ซ้าย/ขวา = " + XText(now);
                Flash($"{(d > 0 ? "▲" : "▼")}  {cfg.Names[gun - 1]}  {label}");
                PopOsd($"{(d > 0 ? "▲" : "▼")}  {label}");
                UpdateGuns();
                saveVals.Stop(); saveVals.Start();
                liveTimer.Stop(); liveTimer.Start();             // ส่งเข้า G Hub ให้เอง
                return;
            }
            SelectGun(gun % GUNS + 1);
        }

        void UpdateHotkeyText()
        {
            HotkeyBtn.Content = KeyName(cfg.HotKey);
            HotkeyBtn.ClearValue(BorderBrushProperty);
            PresetKeyBtn.Content = cfg.PresetKey == 0 ? "ไม่ใช้" : KeyName(cfg.PresetKey);
            PresetKeyBtn.ClearValue(BorderBrushProperty);
            PowerHint.Text = "คลิกปุ่ม หรือกด " + KeyName(cfg.HotKey);
            for (int i = 0; i < 3; i++)
            {
                var b = ScopeBtn(i);
                b.Content = ScopeKeys[i] == 0 ? $"{i + 1}: –" : $"{i + 1}: {KeyName(ScopeKeys[i])}";
                b.ToolTip = $"ปุ่มเลือกชุด {i + 1} ({cfg.Names[i]}) — คลิกแล้วกดปุ่ม, Del = ไม่ใช้";
                b.ClearValue(BorderBrushProperty);
            }
            UpdateCycleText();
            CycleBtn.ClearValue(BorderBrushProperty);
            UpdateLootPage();
        }

        int captureFor;          // กำลังรอปุ่มให้: 0 เปิด/ปิด, 1 สลับโปรไฟล์, 2 วนปืนในเกม, 3–5 ปุ่มเลือกสโคป 1–3

        void Hotkey_Click(object sender, RoutedEventArgs e) => BeginCapture(0);
        void ScopeKey_Click(object sender, RoutedEventArgs e) => BeginCapture(2 + int.Parse((string)((Button)sender).Tag));

        Button ScopeBtn(int i) => i == 0 ? Scope1Btn : i == 1 ? Scope2Btn : Scope3Btn;

        int[] ScopeKeys => cfg.ScopeKeys != null && cfg.ScopeKeys.Length == 3 ? cfg.ScopeKeys : (cfg.ScopeKeys = new int[3]);

        // กดปุ่มเลือกสโคป → ใช้ชุดค่านั้นทันที (บอกบนป้าย)
        bool ScopeKeyPressed(int vk)
        {
            int i = Array.IndexOf(ScopeKeys, vk);
            if (vk == 0 || i < 0 || vals == null || i >= vals.Length) return false;
            LearnScope(i + 1);
            if (i + 1 != gun) SelectGun(i + 1);
            if (!on) PopOsd();
            return true;
        }
        void PresetKey_Click(object sender, RoutedEventArgs e) => BeginCapture(1);

        void BeginCapture(int target)
        {
            bool same = capturing && captureFor == target;
            capturing = false;
            UpdateHotkeyText();
            if (same) return;                            // กดซ้ำ = ยกเลิก
            capturing = true;
            captureFor = target;
            if (target == 6) { UpdateLootPage(); return; }
            var btn = target >= 3 ? ScopeBtn(target - 3) : target == 1 ? PresetKeyBtn : target == 2 ? CycleBtn : HotkeyBtn;
            btn.Content = target >= 3 ? "กดปุ่ม..." : target == 0 ? "กดปุ่ม / ปุ่มเมาส์..." : target == 1 ? "กดปุ่ม... (Del = ไม่ใช้)" : "ในเกม: กดปุ่มคีย์บอร์ด... (Esc = ยกเลิก)";
            btn.BorderBrush = accent;
        }

        void CaptureKey(int vk)
        {
            const int VK_DELETE = 0x2E, VK_BACK = 0x08;
            if (vk == VK_ESC) { capturing = false; UpdateHotkeyText(); return; }
            if (captureFor >= 3 && (vk == VK_DELETE || vk == VK_BACK))
            {
                capturing = false;
                ScopeKeys[captureFor - 3] = 0;
                cfg.Save();
                UpdateHotkeyText();
                Flash($"ไม่ใช้ปุ่มเลือกชุด {captureFor - 2} แล้ว");
                return;
            }
            if (captureFor == 1 && (vk == VK_DELETE || vk == VK_BACK))
            {
                capturing = false;
                cfg.PresetKey = 0;
                cfg.Save();
                UpdateHotkeyText();
                Flash("ปิดปุ่มลัดสลับโปรไฟล์แล้ว");
                return;
            }
            if (!HotkeyAllowed(vk)) { Flash("ใช้ปุ่มนี้ไม่ได้ ลองปุ่มอื่น"); return; }
            var taken = new[] { cfg.HotKey, cfg.PresetKey, cfg.CycleKey }.Concat(ScopeKeys).Where((k, i) => i != captureFor && k != 0).Append(captureFor == 6 ? 0 : cfg.LootKey).Where(k => k != 0);
            if (taken.Contains(vk) || (vk >= 4 && vk <= 6 && cfg.CycleKey == 0 && cfg.CycleMouse == vk - 1 && captureFor != 2))
            {
                Flash("ปุ่มนี้ใช้กับอีกอย่างอยู่แล้ว (เช่น ปุ่มในเกม) ลองปุ่มอื่น");
                return;
            }
            if (cfg.SlotSwitch > 0 && (vk == 0x31 || vk == 0x32))
            {
                Flash("ปุ่ม 1 / 2 ใช้สลับปืนตามช่องอยู่ — ลองปุ่มอื่น หรือปิด \"สลับปืนตามช่อง\" ก่อน");
                return;
            }
            capturing = false;
            if (captureFor == 6)
            {
                cfg.LootKey = vk;
                cfg.Save();
                UpdateHotkeyText();
                Flash("เก็บของเร็ว: เปิดกระเป๋าแล้วกด " + KeyName(vk));
                Sfx.Play(Sfx.Pick, cfg.Sound);
                return;
            }
            if (captureFor == 2) { SetCycle(0, vk); UpdateHotkeyText(); return; }
            if (captureFor >= 3)
            {
                ScopeKeys[captureFor - 3] = vk;
                cfg.Save();
                UpdateHotkeyText();
                Flash($"ปุ่ม {KeyName(vk)} = ชุด {captureFor - 2} ({cfg.Names[captureFor - 3]})");
                Sfx.Play(Sfx.Pick, cfg.Sound);
                return;
            }
            if (captureFor == 1) cfg.PresetKey = vk; else cfg.HotKey = vk;
            cfg.Save();
            UpdateHotkeyText();
            Flash((captureFor == 1 ? "ปุ่มสลับโปรไฟล์ = " : "ตั้งปุ่มลัดเป็น ") + KeyName(vk));
            Sfx.Play(Sfx.Pick, cfg.Sound);
        }

        // ---------------------------------------------------------------- อัปเดตจาก GitHub

        async void CheckUpdate()
        {
            var r = await Updater.CheckAsync();
            if (r == null) return;
            pendingUpdate = r;
            UpdateBtn.Content = "⬆  อัปเดต " + r.Tag;
            UpdateBtn.Visibility = Visibility.Visible;
            if (notifiedTag == r.Tag) return;              // เช็กซ้ำทุก 6 ชม. ไม่ต้องบอกเรื่องเดิมอีก
            notifiedTag = r.Tag;
            Notify("GunBeep มีเวอร์ชันใหม่", r.Tag + " — กดปุ่มอัปเดตที่หน้าต่างโปรแกรม");
        }

        string notifiedTag;

        async void Update_Click(object sender, RoutedEventArgs e)
        {
            var r = pendingUpdate;
            if (r == null || setupBusy || liveBusy) return;
            if (!Ask($"อัปเดต GunBeep จาก v{Updater.Current} เป็น {r.Tag}?\n\n" +
                     "โปรแกรมจะปิด โหลดไฟล์ใหม่มาทับ แล้วเปิดเองอีกครั้ง\n(ค่าที่ตั้งไว้ในโฟลเดอร์ saved ไม่หาย)")) return;
            UpdateBtn.IsEnabled = false;
            UpdateBtn.Content = "กำลังดาวน์โหลด...";
            try
            {
                // เกมเปิดอยู่ → เปิดตัวใหม่ไว้ที่ถาดเงียบๆ ไม่แย่งจอเกม
                bool quiet = GameRunning() || !IsVisible;
                await Updater.StageAsync(r, quiet);
                Log.Info($"update to {r.Tag} (quiet={quiet})");
                Close();                     // OnClosing ปิดมาโคร + เซฟค่า แล้วตัวคัดลอกทำงานต่อ
            }
            catch (Exception ex)
            {
                Tell("อัปเดตไม่สำเร็จ: " + ex.Message);
                UpdateBtn.IsEnabled = true;
                UpdateBtn.Content = "⬆  อัปเดต " + r.Tag;
            }
        }

        // ---------------------------------------------------------------- G Hub

        static readonly Color GOOD = Color.FromRgb(0x22, 0xC5, 0x5E);
        static readonly Color WARN = Color.FromRgb(0xF5, 0x9E, 0x0B);
        static readonly Color BAD = Color.FromRgb(0xEF, 0x44, 0x44);

        async void RefreshGHub()
        {
            GHubCard.Visibility = Native ? Visibility.Collapsed : Visibility.Visible;
            if (Native) { scriptVersion = 999; RefreshApplyButton(); return; }     // ในตัว: มีทุกความสามารถ
            var st = await Task.Run(GHub.GetStatus);
            if (Native) return;
            scriptVersion = st.Script?.IsGunBeep == true ? st.Script.Version : 0;
            RefreshApplyButton();
            GHubRows.Children.Clear();

            if (!st.Installed)
                AddRow(BAD, "ยังไม่ได้ติดตั้ง G Hub");
            else
                AddRow(GOOD, "ติดตั้งแล้ว · " + st.Version);

            if (st.Installed)
            {
                if (!st.ProfileFound) AddRow(WARN, "ยังไม่พบโปรไฟล์ PUBG ใน G Hub");
                else if (st.Locked) AddRow(GOOD, "ล็อกโปรไฟล์ PUBG แล้ว");
                else AddRow(WARN, "ยังไม่ได้ล็อกโปรไฟล์ PUBG");

                if (st.Script == null || !st.ProfileHasScript) AddRow(WARN, "ยังไม่มีสคริปต์ในโปรไฟล์ PUBG");
                else if (st.ScriptUpToDate) AddRow(GOOD, $"สคริปต์ GunBeep v{st.Script.Version} (ล่าสุด)");
                else AddRow(WARN, st.Script.Version == 0 ? "สคริปต์ยังไม่ใช่เวอร์ชันของ GunBeep" : $"สคริปต์ v{st.Script.Version} → อัปเดตเป็น v{st.LatestVersion}");
            }
            if (st.Error != null) AddRow(BAD, "อ่านค่า G Hub ไม่ได้: " + st.Error);

            SetupBtn.Content = st.AllGood ? "ตรวจอีกครั้ง" : "ตั้งค่าอัตโนมัติ";
            SetupBtn.BorderBrush = st.AllGood ? null : accent;
            if (st.AllGood) SetupBtn.ClearValue(BorderBrushProperty);
        }

        void AddRow(Color c, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Fill = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 8, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = text, FontSize = 11.5, Foreground = nameIdle, TextWrapping = TextWrapping.Wrap, MaxWidth = 215
            });
            GHubRows.Children.Add(row);
        }

        bool Ask(string text) =>
            MessageBox.Show(this, text, "GunBeep", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        void Tell(string text) =>
            MessageBox.Show(this, text, "GunBeep", MessageBoxButton.OK, MessageBoxImage.Information);

        async void Setup_Click(object sender, RoutedEventArgs e)
        {
            if (setupBusy) return;
            setupBusy = true;
            SetupBtn.IsEnabled = false;
            SetupBtn.Content = "กำลังทำ...";
            bool stopped = false;
            try
            {
                var st = await Task.Run(GHub.GetStatus);

                // 1) ติดตั้ง G Hub
                if (!st.Installed)
                {
                    string inst = GHub.FindInstaller();
                    if (inst == null)
                    {
                        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "เลือกไฟล์ติดตั้ง Logitech G Hub", Filter = "ตัวติดตั้ง (*.exe)|*.exe" };
                        if (dlg.ShowDialog(this) != true) return;
                        inst = dlg.FileName;
                    }
                    if (!await Task.Run(() => GHub.IsLogitechSigned(inst)))
                    {
                        Tell("ไฟล์นี้ไม่ใช่ตัวติดตั้งของ Logitech ที่มีลายเซ็นถูกต้อง\n" + inst);
                        return;
                    }
                    if (!Ask("ยังไม่ได้ติดตั้ง G Hub\nจะเปิดตัวติดตั้งนี้ให้:\n" + inst + "\n\nดำเนินการต่อ?")) return;
                    Flash("กำลังติดตั้ง G Hub...");
                    try { await GHub.RunInstallerAsync(inst); }
                    catch (Win32Exception) { return; }   // กดยกเลิก UAC
                    st = await Task.Run(GHub.GetStatus);
                    if (!st.Installed) { Tell("ยังติดตั้ง G Hub ไม่เสร็จ\nติดตั้งให้เสร็จแล้วกดปุ่มนี้อีกครั้ง"); return; }
                }

                // 2) ต้องมีโปรไฟล์ PUBG และสคริปต์อย่างน้อยหนึ่งตัว (G Hub ไม่ให้สร้างจากข้างนอก)
                if (!st.ProfileFound || st.Script == null || !st.ProfileHasScript)
                {
                    Tell("ครั้งแรกต้องสร้างสคริปต์ใน G Hub เองหนึ่งครั้ง:\n\n" +
                         "1. เปิด G Hub → เลือกเกม PUBG (ถ้ายังไม่มี ให้เปิดเกมหนึ่งครั้ง G Hub จะเพิ่มให้)\n" +
                         "2. กดไอคอน Scripting ใต้โปรไฟล์ → Create new Lua script\n" +
                         "3. ปิดหน้าสคริปต์ แล้วกลับมากดปุ่มนี้อีกครั้ง\n\nจะเปิด G Hub ให้ตอนนี้");
                    GHub.OpenWindow();
                    return;
                }

                bool needScript = !st.ScriptUpToDate;
                bool needLock = !st.Locked;
                if (!needScript && !needLock) { Flash("G Hub พร้อมใช้งานแล้ว"); Sfx.Play(Sfx.Pick, cfg.Sound); return; }

                string what = (needScript ? "• ใส่สคริปต์ GunBeep v" + st.LatestVersion + " (เก็บค่า base / x เดิมไว้)\n" : "")
                            + (needLock ? "• ล็อกโปรไฟล์ PUBG\n" : "");
                if (!Ask("จะทำสิ่งนี้ให้:\n" + what +
                         "\nต้องปิดแล้วเปิด G Hub ใหม่ (ประมาณ 10 วินาที)\nระหว่างนี้เมาส์จะกลับเป็นค่าปกติ อย่าเพิ่งเข้าเกม\n" +
                         "ไฟล์เดิมสำรองไว้ที่ %APPDATA%\\GunBeep\\backups\n\nดำเนินการต่อ?")) return;

                if (on) ToggleOn();
                Flash("กำลังรีสตาร์ท G Hub...");
                stopped = true;
                if (saveVals.IsEnabled) saveVals.Stop();
                if (vals != null) { var copy = Clone(vals); QueueFile("save guns", () => GHub.WriteGunValues(copy)); }
                await fileWork;                       // ค่าล่าสุดลงไฟล์ก่อน ตัวติดตั้งจะได้เก็บค่านั้นไว้
                int cycleBtn = cfg.CycleMouse;
                await Task.Run(async () =>
                {
                    await GHub.StopAsync();
                    if (needScript) { GHub.InstallScript(st.Script); GHub.WriteCycleButton(cycleBtn); }
                    if (needLock) GHub.LockPubgProfile();
                    GHub.StartBackground();
                    await GHub.WaitAgentAsync(25);
                });
                stopped = false;
                Flash("ตั้งค่า G Hub เสร็จแล้ว");
                Sfx.Play(Sfx.Ready, cfg.Sound);
            }
            catch (Exception ex)
            {
                Tell("เกิดข้อผิดพลาด: " + ex.Message);
            }
            finally
            {
                if (stopped && !GHub.IsRunning) try { GHub.StartBackground(); } catch { }
                setupBusy = false;
                SetupBtn.IsEnabled = true;
                RefreshGHub();
                LoadGunValues();
            }
        }

        // ---------------------------------------------------------------- หน้าจอ

        void UpdatePower()
        {
            var dur = firstPower ? TimeSpan.Zero : TimeSpan.FromMilliseconds(320);
            firstPower = false;
            powerBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? ON : OFF, dur));

            PowerText.Text = on ? "ON" : "OFF";
            StatusText.Text = on ? "มาโครทำงานอยู่" : "มาโครปิดอยู่";

            ambientOn = null;                // ให้ UpdateAmbient ตั้งเรืองแสงของปุ่มใหม่ตามสถานะ
            UpdateAmbient();
            UpdateTray();
        }

        bool? ambientOn;

        static T Slow<T>(T t) where T : Timeline { Timeline.SetDesiredFrameRate(t, 24); return t; }

        void UpdateAmbient()
        {
            bool run = IsVisible && IsActive && WindowState != WindowState.Minimized;
            if (ambientOn == run) return;
            ambientOn = run;
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            if (run)
            {
                EdgeSpin.BeginAnimation(RotateTransform.AngleProperty, Slow(new DoubleAnimation(EdgeSpin.Angle, EdgeSpin.Angle + 360,
                    TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever }));
                LogoGlow.BeginAnimation(OpacityProperty, Slow(new DoubleAnimation(0.25, 0.65, TimeSpan.FromSeconds(1.8))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease }));
                Glow.BeginAnimation(OpacityProperty, on
                    ? Slow(new DoubleAnimation(0.35, 0.75, TimeSpan.FromSeconds(1.4))
                        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease })
                    : new DoubleAnimation(0.18, TimeSpan.FromMilliseconds(300)));
            }
            else
            {
                // หยุดนิ่งตรงตำแหน่งปัจจุบัน
                double angle = EdgeSpin.Angle;
                EdgeSpin.BeginAnimation(RotateTransform.AngleProperty, null);
                EdgeSpin.Angle = angle % 360;
                LogoGlow.BeginAnimation(OpacityProperty, null);
                LogoGlow.Opacity = 0.45;
                Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0.6 : 0.18, TimeSpan.FromMilliseconds(300)));
            }
        }

        void BuildCards()
        {
            for (int i = 0; i < GUNS; i++)
            {
                int idx = i + 1;

                var badgeText = new TextBlock
                {
                    Text = idx.ToString(), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                badges[i] = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(7), Child = badgeText,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(10, 10, 0, 0)
                };

                nameTexts[i] = new TextBlock
                {
                    Text = cfg.Names[i], FontSize = 15.5, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 6, 8, 12)
                };

                valTexts[i] = new TextBlock
                {
                    FontSize = 10.5, FontFamily = new FontFamily("Consolas, Segoe UI"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(4, 0, 4, 9)
                };

                var edit = new TextBox
                {
                    Visibility = Visibility.Collapsed, FontSize = 13.5, Margin = new Thickness(8, 22, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x16)), Foreground = Brushes.White,
                    CaretBrush = Brushes.White, BorderBrush = accent, Padding = new Thickness(2, 3, 2, 3), MaxLength = 14
                };
                edit.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter) EndRename(idx, true);
                    else if (e.Key == Key.Escape) EndRename(idx, false);
                };
                edit.LostKeyboardFocus += (s, e) => EndRename(idx, true);
                nameEdits[i] = edit;

                var grid = new Grid();
                grid.Children.Add(badges[i]);
                grid.Children.Add(nameTexts[i]);
                grid.Children.Add(valTexts[i]);
                grid.Children.Add(edit);

                var card = new Border
                {
                    Height = 88, Margin = new Thickness(5), CornerRadius = new CornerRadius(16),
                    BorderThickness = new Thickness(1.5), Child = grid, Cursor = Cursors.Hand
                };
                card.MouseEnter += (s, e) => { if (idx != gun) card.BorderBrush = cardHover; };
                card.MouseLeave += (s, e) => { if (idx != gun) card.BorderBrush = cardLine; };
                card.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount >= 2) BeginRename(idx);
                    else SelectGun(idx);
                };
                cards[i] = card;
                GunGrid.Children.Add(card);
            }
        }

        void UpdateGuns()
        {
            GunGrid.Opacity = on ? 1.0 : 0.7;
            for (int i = 0; i < GUNS; i++)
            {
                bool active = i + 1 == gun;
                var card = cards[i];
                card.Background = active ? activeBg : cardBg;
                card.BorderBrush = active ? accent : cardLine;
                card.Effect = active && on
                    ? new DropShadowEffect { Color = Color.FromRgb(0x1D, 0x6B, 0xFF), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.65 }
                    : new DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 4, Direction = 270, Opacity = 0.45 };
                badges[i].Background = active ? accent : badgeIdle;
                nameTexts[i].Foreground = active ? Brushes.White : nameIdle;
                nameTexts[i].Text = cfg.Names[i];
                valTexts[i].Foreground = active ? accentText : nameIdle;

                if (active && on)
                {
                    var st = new ScaleTransform(0.94, 0.94);
                    card.RenderTransformOrigin = new Point(0.5, 0.5);
                    card.RenderTransform = st;
                    var pop = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260))
                    { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                }
                else
                {
                    card.RenderTransform = Transform.Identity;
                }
            }
            UpdateValueTexts();
            UpdateTunePanel();
            UpdateTray();
            UpdateOsd();
        }

        void BeginRename(int idx)
        {
            var edit = nameEdits[idx - 1];
            nameTexts[idx - 1].Visibility = Visibility.Collapsed;
            edit.Text = cfg.Names[idx - 1];
            edit.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => { edit.Focus(); edit.SelectAll(); }));
        }

        void EndRename(int idx, bool commit)
        {
            var edit = nameEdits[idx - 1];
            if (edit.Visibility != Visibility.Visible) return;
            if (commit && edit.Text.Trim().Length > 0)
            {
                cfg.Names[idx - 1] = edit.Text.Trim();
                cfg.Save();
            }
            edit.Visibility = Visibility.Collapsed;
            nameTexts[idx - 1].Visibility = Visibility.Visible;
            UpdateGuns();
        }

        void Flash(string text)
        {
            EventText.Text = text;
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.9))));
            EventText.BeginAnimation(OpacityProperty, a);
        }

        // ---------------------------------------------------------------- ถาดไอคอน

        void SetupTray()
        {
            tray = new System.Windows.Forms.NotifyIcon { Visible = true, Text = "GunBeep" };
            tray.DoubleClick += (s, e) => ShowFromTray();
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("เปิดหน้าต่าง", null, (s, e) => ShowFromTray());
            menu.Items.Add("ออกจากโปรแกรม", null, (s, e) => Close());
            tray.ContextMenuStrip = menu;
        }

        void UpdateTray()
        {
            if (tray == null) return;
            var old = tray.Icon;
            tray.Icon = TrayArt.Make(on, gun, out var winIcon);
            Icon = winIcon;
            old?.Dispose();
            string t = "all@Ben-GunBeep · " + (on ? "ON" : "OFF") + " · " + cfg.Names[gun - 1];
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        public void ShowFromTray()
        {
            bool wasHidden = !IsVisible;
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (wasHidden) AnimateIn();
        }

        // เปิดหน้าต่าง: ค่อยๆ ลอยขึ้น + จางเข้า
        void AnimateIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var t = TimeSpan.FromMilliseconds(280);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, t) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.965, 1, t) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.965, 1, t) { EasingFunction = ease });
            RootShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, t) { EasingFunction = ease });
        }

        void AnimateOut(Action done)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var t = TimeSpan.FromMilliseconds(170);
            var fade = new DoubleAnimation(0, t) { EasingFunction = ease };
            fade.Completed += (s, e) => done();
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, t) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, t) { EasingFunction = ease });
            RootShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, t) { EasingFunction = ease });
            Root.BeginAnimation(OpacityProperty, fade);
        }

        // ---------------------------------------------------------------- ปุ่มในหน้าต่าง

        void Title_Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void Min_Click(object sender, RoutedEventArgs e)
        {
            AnimateOut(Hide);
            if (!hintShown)
            {
                hintShown = true;
                tray.ShowBalloonTip(2500, "GunBeep ยังทำงานอยู่", "ดับเบิลคลิกไอคอนที่ถาดเพื่อเปิดหน้าต่าง",
                                    System.Windows.Forms.ToolTipIcon.None);
            }
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void Power_Down(object sender, MouseButtonEventArgs e) => AnimatePress(0.95);
        void Power_Leave(object sender, MouseEventArgs e) => AnimatePress(1);
        void Power_Up(object sender, MouseButtonEventArgs e)
        {
            AnimatePress(1);
            ToggleOn();
        }

        void AnimatePress(double to)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(110));
            PowerScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            PowerScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // ปิดโปรแกรม = ปิดมาโครด้วย (ดับไฟทั้งสองดวง)
            if (saveVals.IsEnabled) FlushGunValues();
            else if (!fileWork.Wait(4000)) Log.Error("file writes still running at exit");
            Log.Info("exit");
            on = false;
            ApplyState();
            engine?.Dispose();
            if (!double.IsNaN(Left) && !double.IsNaN(Top) && Left >= 0 && Top >= 0)
            {
                cfg.Left = Left;
                cfg.Top = Top;
            }
            cfg.Save();
            Socd.SetActive(false);
            hook?.Dispose();
            osd?.Close();
            calibWin?.Close();
            visionWin?.Close();
            eye?.Dispose();
            if (resSwitched) RestoreRes();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnClosing(e);
        }
    }
}
