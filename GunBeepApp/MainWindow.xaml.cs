using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
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

        public MainWindow()
        {
            InitializeComponent();

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
                && cfg.Left.Value > SystemParameters.VirtualScreenLeft - 100
                && cfg.Left.Value < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
                && cfg.Top.Value > SystemParameters.VirtualScreenTop - 20
                && cfg.Top.Value < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = cfg.Left.Value;
                Top = cfg.Top.Value;
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

            // ดูว่า PUBG เปิด/ปิดหรือยัง (เปลี่ยนความละเอียดจอให้) — เปิดอยู่ก่อน GunBeep = ไม่ยุ่ง (ยกเว้นโหมดยืดตลอด)
            UpdateResButton();
            gameWas = GameRunning();
            if (cfg.AutoRes && cfg.ResAlways) SwitchRes();
            gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            gameTimer.Tick += (s, e) => WatchGame();
            gameTimer.Start();

            // เปิดโปรแกรมมาให้มาโครปิดไว้ก่อนเสมอ
            scroll = KeyHook.IsLockOn(VK_SCROLL);
            num = KeyHook.IsLockOn(VK_NUM);
            on = false;
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

            // G Hub รีสตาร์ทเมื่อไหร่ สคริปต์โหลดค่าจากไฟล์ใหม่ → อ่านค่าตามไปด้วย
            agentWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            agentWatch.Tick += (s, e) =>
            {
                int pid = GHub.AgentPid();
                if (pid != 0 && pid != agentPid) { agentPid = pid; LoadGunValues(); }
            };
            agentPid = GHub.AgentPid();
            agentWatch.Start();

            hook = new KeyHook();
            hook.KeyDown += vk => Dispatcher.BeginInvoke(new Action(() => OnKey(vk)));

            SubTitle.Text = "Recoil Control Panel · v" + Updater.Current;

            // เช็กอัปเดตจาก GitHub ตอนเปิด และทุก 6 ชั่วโมง
            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            updateTimer.Tick += (s, e) => CheckUpdate();
            updateTimer.Start();

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
            if (now == gameWas) { KeepRes(); return; }
            gameWas = now;
            Log.Info(now ? "PUBG started" : "PUBG closed");
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
            calibWin = new Calibrator(cfg.Names.Take(vals.Length).ToArray(), gun, () => on ? gun : 0, InMacro, ApplyCalibration,
                                      msg => { PopOsd(msg); Sfx.Play(Sfx.Pick, cfg.Sound); }) { Owner = this };
            calibWin.Closed += (s, a) => calibWin = null;
            calibWin.Show();
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
        void ApplyState()
        {
            int st = on ? gun : 0;
            bool wantScroll = st == 1 || st == 3;
            bool wantNum = st == 2 || st == 3;
            if (wantScroll && !scroll) { KeyHook.Tap(VK_SCROLL, 0x46, false); scroll = true; }
            if (wantNum && !num) { KeyHook.Tap(VK_NUM, 0x45, true); num = true; }
            if (!wantScroll && scroll) { KeyHook.Tap(VK_SCROLL, 0x46, false); scroll = false; }
            if (!wantNum && num) { KeyHook.Tap(VK_NUM, 0x45, true); num = false; }
        }

        // ไฟถูกเปลี่ยนจากข้างนอก (G Hub วนปืนด้วยปุ่ม Back)
        void SyncFromLocks()
        {
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

        void OnKey(int vk)
        {
            if (capturing) { CaptureKey(vk); return; }

            if (vk == cfg.HotKey) { ToggleOn(); return; }
            if (cfg.PresetKey != 0 && vk == cfg.PresetKey) { CyclePreset(); return; }

            switch (vk)
            {
                case VK_SCROLL:
                case VK_NUM:
                    if (vk == VK_SCROLL) scroll = !scroll; else num = !num;
                    lockSettle.Stop();
                    lockSettle.Start();
                    break;

                case 0x81: liveAck?.TrySetResult(true); break;   // F18 = G Hub รับค่าแล้ว
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
                BaseSlider.Maximum = Math.Max(20, Math.Ceiling(v.Base));
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
            bool live = scriptVersion >= LiveLink.SEGS_VERSION;
            var res = SegsEditor.Edit(this, cfg.Names.Take(vals.Length).ToArray(),
                                      vals.Select(v => v.Steps).ToArray(), vals.Select(v => v.Base).ToArray(), gun, live);
            if (res == null) return;
            // ปืนที่แก้ = ปัดเป็นแท่งละ 100 ms ค่าละเอียด 0.25 (แบบเดียวกับที่ส่งเข้า G Hub)
            for (int i = 0; i < vals.Length; i++)
                if (!GHub.SameSteps(res[i], vals[i].Steps)) vals[i].Steps = LiveLink.Fit(res[i]);
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
              || vk < 0x08);

        static string KeyName(int vk)
        {
            string s = KeyInterop.KeyFromVirtualKey(vk).ToString();
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

        void UpdateHotkeyText()
        {
            HotkeyBtn.Content = KeyName(cfg.HotKey);
            HotkeyBtn.ClearValue(BorderBrushProperty);
            PresetKeyBtn.Content = cfg.PresetKey == 0 ? "ไม่ใช้" : KeyName(cfg.PresetKey);
            PresetKeyBtn.ClearValue(BorderBrushProperty);
            PowerHint.Text = "คลิกปุ่ม หรือกด " + KeyName(cfg.HotKey);
        }

        bool capturePreset;      // กำลังรอปุ่มให้ "สลับโปรไฟล์" (ไม่งั้น = ปุ่มเปิด/ปิด)

        void Hotkey_Click(object sender, RoutedEventArgs e) => BeginCapture(false);
        void PresetKey_Click(object sender, RoutedEventArgs e) => BeginCapture(true);

        void BeginCapture(bool forPreset)
        {
            bool same = capturing && capturePreset == forPreset;
            capturing = false;
            UpdateHotkeyText();
            if (same) return;                            // กดซ้ำ = ยกเลิก
            capturing = true;
            capturePreset = forPreset;
            var btn = forPreset ? PresetKeyBtn : HotkeyBtn;
            btn.Content = forPreset ? "กดปุ่ม... (Del = ไม่ใช้)" : "กดปุ่ม...";
            btn.BorderBrush = accent;
        }

        void CaptureKey(int vk)
        {
            const int VK_DELETE = 0x2E, VK_BACK = 0x08;
            if (vk == VK_ESC) { capturing = false; UpdateHotkeyText(); return; }
            if (capturePreset && (vk == VK_DELETE || vk == VK_BACK))
            {
                capturing = false;
                cfg.PresetKey = 0;
                cfg.Save();
                UpdateHotkeyText();
                Flash("ปิดปุ่มลัดสลับโปรไฟล์แล้ว");
                return;
            }
            if (!HotkeyAllowed(vk)) { Flash("ใช้ปุ่มนี้ไม่ได้ ลองปุ่มอื่น"); return; }
            if (vk == (capturePreset ? cfg.HotKey : cfg.PresetKey))
            {
                Flash("ปุ่มนี้ใช้กับอีกอย่างอยู่แล้ว ลองปุ่มอื่น");
                return;
            }
            capturing = false;
            if (capturePreset) cfg.PresetKey = vk; else cfg.HotKey = vk;
            cfg.Save();
            UpdateHotkeyText();
            Flash((capturePreset ? "ปุ่มสลับโปรไฟล์ = " : "ตั้งปุ่มลัดเป็น ") + KeyName(vk));
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
            var st = await Task.Run(GHub.GetStatus);
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
                await Task.Run(async () =>
                {
                    await GHub.StopAsync();
                    if (needScript) GHub.InstallScript(st.Script);
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
            if (!double.IsNaN(Left) && !double.IsNaN(Top))   // เปิดจากถาดแล้วไม่เคยโชว์หน้าต่าง = ไม่มีตำแหน่ง
            {
                cfg.Left = Left;
                cfg.Top = Top;
            }
            cfg.Save();
            hook?.Dispose();
            osd?.Close();
            calibWin?.Close();
            if (resSwitched) RestoreRes();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnClosing(e);
        }
    }
}
