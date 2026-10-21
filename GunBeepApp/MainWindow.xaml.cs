using System;
using System.ComponentModel;
using System.Linq;
using System.Speech.Synthesis;
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
        SpeechSynthesizer tts;
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
            // ขอบไฟฟ้าหมุนช้าๆ + โลโก้เรืองแสงเป็นจังหวะ
            EdgeSpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever });
            LogoGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 0.65, TimeSpan.FromSeconds(1.8))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
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
            XSlider.ValueChanged += (s, e) => SliderMoved("x", e.NewValue);
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

            try { tts = new SpeechSynthesizer { Rate = 1, Volume = 100 }; } catch { tts = null; }

            SetupTray();
            osd = new Overlay();
            UpdateOsdButtons();

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
                Task.Run(() => { try { GHub.WriteGunValues(copy); } catch { } });
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
                Announce();
                PopOsd();
            }
        }

        // ---------------------------------------------------------------- ปุ่มที่ดักได้

        void OnKey(int vk)
        {
            if (capturing) { CaptureKey(vk); return; }

            if (vk == cfg.HotKey) { ToggleOn(); return; }

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
            vals = await Task.Run(GHub.ReadGunValues);
            applied = Clone(vals);                                  // G Hub โหลดค่าจากไฟล์เดียวกันนี้
            liveFailed = false;
            UpdateGuns();
        }

        static GHub.GunVals[] Clone(GHub.GunVals[] src) =>
            src?.Select(v => new GHub.GunVals { Base = v.Base, X = v.X }).ToArray();

        bool HasPending =>
            vals != null && applied != null &&
            vals.Zip(applied).Any(p => Math.Abs(p.First.Base - p.Second.Base) > 1e-9 || Math.Abs(p.First.X - p.Second.X) > 1e-9);

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
                XSlider.Maximum = Math.Max(5, Math.Ceiling(v.X));
                XSlider.Minimum = Math.Min(-5, Math.Floor(v.X));
                BaseSlider.Value = v.Base;
                XSlider.Value = v.X;
                BaseValue.Text = GHub.Fmt(v.Base);
                XValue.Text = GHub.Fmt(v.X);
            }
            else
            {
                BaseValue.Text = XValue.Text = "–";
            }
            syncingSliders = false;
            RefreshApplyButton();
        }

        void RefreshApplyButton()
        {
            bool have = vals != null && gun <= vals.Length;
            bool pending = HasPending;
            ApplyBtn.IsEnabled = pending && !setupBusy && !liveBusy;
            ApplyBtn.Content = setupBusy ? "กำลังรีสตาร์ท G Hub..."
                             : liveBusy ? "กำลังส่งค่าเข้า G Hub..."
                             : pending && liveFailed ? "ใช้ค่านี้  (รีสตาร์ท G Hub ~10 วิ)"
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
        }

        void SliderMoved(string field, double value)
        {
            if (syncingSliders || vals == null || gun > vals.Length) return;
            value = Math.Round(value * 2) / 2;                       // ทีละ 0.5
            var v = vals[gun - 1];
            if (field == "base") { if (v.Base == value) return; v.Base = value; BaseValue.Text = GHub.Fmt(value); }
            else { if (v.X == value) return; v.X = value; XValue.Text = GHub.Fmt(value); }
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
            bool ok = true;
            try
            {
                do
                {
                    liveAgain = false;
                    for (int i = 0; i < Math.Min(vals.Length, applied.Length); i++)
                    {
                        var want = Snap(vals[i]);
                        if (Same(want, applied[i])) continue;
                        var bits = LiveLink.Frame(i + 1, want.Base, want.X);
                        liveAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await Task.Run(() => LiveLink.Send(bits));
                        var done = await Task.WhenAny(liveAck.Task, Task.Delay(1000));
                        if (done != liveAck.Task) { ok = false; break; }
                        applied[i] = want;
                    }
                } while (ok && liveAgain);
            }
            finally
            {
                liveAck = null;
                liveBusy = false;
            }
            liveFailed = !ok;
            Flash(ok ? "ส่งค่าเข้า G Hub แล้ว (ไม่ต้องรีสตาร์ท)" : "G Hub ไม่ตอบรับ — สคริปต์อาจยังไม่ใช่ v5");
            if (ok) Sfx.Play(Sfx.Pick, cfg.Sound);
            PopOsd(ok ? "✓  ส่งค่าเข้า G Hub แล้ว" : "✗  G Hub ไม่ตอบรับ");
            RefreshApplyButton();
            return ok;
        }

        async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (!HasPending) return;
            if (!liveFailed && await ApplyLiveAsync()) return;
            if (Ask("ส่งค่าเข้า G Hub แบบ real time ไม่สำเร็จ\n(สคริปต์ใน G Hub อาจยังไม่ใช่ v5 — กด \"ตั้งค่าอัตโนมัติ\" เพื่ออัปเดต)\n\n" +
                    "จะใช้วิธีรีสตาร์ท G Hub (~10 วินาที) แทนไหม?"))
                await ApplyRestartAsync();
        }

        void Step_Click(object sender, RoutedEventArgs e)
        {
            var tag = (string)((FrameworkElement)sender).Tag;      // "base+", "x-", ...
            var slider = tag.StartsWith("base") ? BaseSlider : XSlider;
            double d = tag.EndsWith("+") ? 0.5 : -0.5;
            if (!slider.IsEnabled) return;
            double target = slider.Value + d;
            if (target > slider.Maximum) slider.Maximum = target;
            if (target < slider.Minimum) slider.Minimum = target;
            slider.Value = target;
        }

        // ทางสำรอง: เขียนค่าลงไฟล์แล้วรีสตาร์ท G Hub ให้โหลดใหม่
        async Task ApplyRestartAsync()
        {
            if (setupBusy || liveBusy || vals == null || !HasPending) return;
            setupBusy = true;
            UpdateTunePanel();
            var copy = Clone(vals);
            bool stopped = false;
            try
            {
                saveVals.Stop();
                Flash("กำลังรีสตาร์ท G Hub เพื่อใช้ค่าใหม่...");
                stopped = true;
                await Task.Run(async () =>
                {
                    GHub.WriteGunValues(copy);
                    await GHub.StopAsync();
                    GHub.StartBackground();
                    await GHub.WaitAgentAsync(25);
                });
                stopped = false;
                agentPid = GHub.AgentPid();
                applied = Clone(copy);
                liveFailed = false;
                Flash("ใช้ค่าใหม่แล้ว");
                Sfx.Play(Sfx.Ready, cfg.Sound);
            }
            catch (Exception ex)
            {
                Tell("ใช้ค่าใหม่ไม่สำเร็จ: " + ex.Message);
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
            if (vals == null) return;
            var copy = vals.Select(v => new GHub.GunVals { Base = v.Base, X = v.X }).ToArray();
            try { GHub.WriteGunValues(copy); } catch { }
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
            if (changed) { Announce(); PopOsd(); }
        }

        void Announce()
        {
            if (!cfg.Sound) return;
            string name = cfg.Names[gun - 1];
            bool ascii = name.All(c => c < 128);
            if (tts == null) { Sfx.Play(Sfx.Pick, true); return; }
            tts.SpeakAsyncCancelAll();
            tts.SpeakAsync(ascii && name.Trim().Length > 0 ? name : "Gun " + gun);
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
            PowerHint.Text = "คลิกปุ่ม หรือกด " + KeyName(cfg.HotKey);
        }

        void Hotkey_Click(object sender, RoutedEventArgs e)
        {
            if (capturing) { capturing = false; UpdateHotkeyText(); return; }
            capturing = true;
            HotkeyBtn.Content = "กดปุ่ม...";
            HotkeyBtn.BorderBrush = accent;
        }

        void CaptureKey(int vk)
        {
            if (vk == VK_ESC) { capturing = false; UpdateHotkeyText(); return; }
            if (!HotkeyAllowed(vk)) { Flash("ใช้ปุ่มนี้ไม่ได้ ลองปุ่มอื่น"); return; }
            capturing = false;
            cfg.HotKey = vk;
            cfg.Save();
            UpdateHotkeyText();
            Flash("ตั้งปุ่มลัดเป็น " + KeyName(vk));
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
            if (tray != null)
                tray.ShowBalloonTip(3000, "GunBeep มีเวอร์ชันใหม่", r.Tag + " — กดปุ่มอัปเดตที่หน้าต่างโปรแกรม",
                                    System.Windows.Forms.ToolTipIcon.None);
        }

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
                await Updater.StageAsync(r);
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

            if (on)
            {
                var pulse = new DoubleAnimation(0.35, 0.75, TimeSpan.FromSeconds(1.4))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Glow.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.18, TimeSpan.FromMilliseconds(300)));
            }
            UpdateTray();
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
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnClosing(e);
        }
    }
}
