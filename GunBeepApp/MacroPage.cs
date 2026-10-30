using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GunBeep
{
    // หน้าใหม่แบบเมนูซ้าย: มาโคร (ปืนที่ตรวจเจอแบบสด) · จูนแรงดีด · เก็บของเร็ว · ตั้งค่า
    //   + ตัวขยับเมาส์ในตัว (ไม่ใช้ G Hub) และเปิดมาโครเองตอนเข้าเกม
    public partial class MainWindow
    {
        static Brush B(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        static readonly Brush PText = B("#EAF2FF"), PMuted = B("#8094BC"), PFaint = B("#4E5D80"), PLine = B("#18223A"),
                              PCard = B("#0D1322"), PInner = B("#0A1120"), PBlue = B("#1D6BFF"), PCyan = B("#5CE1FF"),
                              PGold = B("#C9A54A"), PGreen = B("#22C55E"), PRed = B("#EF4444"), PAmber = B("#F59E0B"),
                              PGreenBg = B("#0F2A1C"), PRedBg = B("#2A1416"), PAmberBg = B("#2A2010"), PBlueBg = B("#0F2147");
        static readonly FontFamily Mono = new FontFamily("Consolas"), Big = new FontFamily("Bahnschrift, Segoe UI");

        string pauseWhy;

        // ---------------------------------------------------------------- เริ่มต้น
        void InitPages()
        {
            NavMacro.IsChecked = true;                    // เปิดมาที่หน้ามาโครเสมอ (ไม่ต้องจูนอะไร)
            AutoOnSwitch.IsChecked = cfg.AutoOn;
            GameSettings.Changed += () => Dispatcher.BeginInvoke(new Action(OnGameSettingsChanged));
            try { RawMouse.Shared.Input += LearnInput; } catch (Exception ex) { Log.Error("learn input", ex); }
            ViewTracker.Shared.Start();
            engine.SprayEnded += (y, ms, ads, id) => Dispatcher.BeginInvoke(new Action(() => LearnSpray(y, ms, ads, id)));
            // (ปืนเคาะไม่เรียนจากมือแล้ว: ใช้แรงต่อนัดที่คำนวณ)
            BuildGameCard();
            HolsterSwitch.IsChecked = cfg.HolsterPause;
            UpdateEngineUi();
            BuildKeyCards();
            BuildLootSteps();
            UpdateLootPage();
            LiveDirty();
        }

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            int i = int.Parse((string)((FrameworkElement)sender).Tag);
            var pages = new FrameworkElement[] { PageMacro, PageTune, PageLoot, PageSettings };
            for (int k = 0; k < pages.Length; k++) pages[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
            if (cfg.Page != i) { cfg.Page = i; cfg.Save(); }
            if (i == 0) LiveDirty();
        }

        // ---------------------------------------------------------------- เปิดเองตอนเข้าเกม
        void AutoPower(bool gameRunning)
        {
            if (!cfg.AutoOn) return;
            if (gameRunning != on)
            {
                Log.Info("auto power " + (gameRunning ? "on (PUBG running)" : "off (PUBG closed)"));
                ToggleOn();
            }
        }

        void AutoOn_Click(object sender, RoutedEventArgs e)
        {
            cfg.AutoOn = AutoOnSwitch.IsChecked == true;
            cfg.Save();
            Flash(cfg.AutoOn ? "เปิด PUBG แล้วมาโครเปิดเอง ปิดเกมแล้วปิดเอง" : "เปิด/ปิดมาโครเองด้วยปุ่มลัด");
            if (cfg.AutoOn) AutoPower(gameWas);
            BuildKeyCards();
        }

        void Holster_Click(object sender, RoutedEventArgs e)
        {
            cfg.HolsterPause = HolsterSwitch.IsChecked == true;
            cfg.Save();
            Flash(cfg.HolsterPause ? "ถือระเบิด/มีด/เก็บปืน = ไม่ดึง" : "ดึงทุกครั้งที่ยิง ไม่สนว่าถืออะไร");
        }

        // ---------------------------------------------------------------- ตัวขยับเมาส์ในตัว
        void PushEngine()
        {
            if (engine == null) return;
            var def = vals != null && vals.Any(v => v.Steps == null) ? GHub.DefaultSteps() : null;
            engine.SetGuns(vals?.Select(v => new NativeRecoil.Gun { Rc = v.Steps ?? def, Base = v.Base, X = v.X }).ToArray());
            engine.SetGun(gun);
            engine.RequireAds = cfg.RequireAds;
            engine.MaxShots = EffectiveMaxShots;
            engine.SetGear(multWant[0]);
            engine.SetStance(multWant[1]);
            engine.SetActive(Native && on && !paused);
            PushSemi();
            LiveDirty();
        }

        // ในตัว: "รีสตาร์ท G Hub" = แค่เขียนไฟล์แล้วใช้ทันที
        void SaveNative(double[][] segs)
        {
            if (vals == null) return;
            if (segs != null)
                for (int i = 0; i < Math.Min(segs.Length, vals.Length); i++) if (segs[i] != null) vals[i].Steps = segs[i];
            var all = vals.Select(v => (v.Steps ?? GHub.DefaultSteps()).ToArray()).ToArray();
            var copy = Clone(vals);
            QueueFile("save engine", () => { GHub.WriteSegs(all); GHub.WriteGunValues(copy); });
            applied = Clone(vals);
            appliedSteps = all;
            liveFailed = false;
            PushEngine();
            RefreshApplyButton();
            Flash("ใช้ค่าใหม่แล้ว");
            Sfx.Play(Sfx.Pick, cfg.Sound);
        }

        void UpdateEngineUi()
        {
            EngineNativeRb.IsChecked = Native;
            EngineGHubRb.IsChecked = !Native;
            EngineBadge.Text = Native ? "ENGINE ในตัว · ไม่ใช้ G HUB" : "ENGINE G HUB";
            EngineNote.Text = Native
                ? "GunBeep ขยับเมาส์เอง (SendInput) ไม่ต้องเปิด G Hub — ใช้ค่าใหม่ทันทีไม่มีรีสตาร์ท · Windows ติดป้ายว่าเป็นเมาส์จำลอง ระบบกันโกงมองเห็นได้ง่ายกว่าแบบ G Hub"
                : "G Hub ขยับเมาส์ตามสคริปต์ GunBeep (ต้องเปิด G Hub และตั้งสคริปต์ให้พร้อม)";
            GHubCard.Visibility = Native ? Visibility.Collapsed : Visibility.Visible;
            ApplyBtn.ToolTip = Native ? "ใช้ค่านี้ทันที (ปล่อยหลอดเลื่อนแล้วใช้ให้เอง)" : "ส่งค่าเข้า G Hub ทันที ไม่ต้องรีสตาร์ท (ปล่อยหลอดเลื่อนแล้วจะส่งให้เอง)";
        }

        async void Engine_Checked(object sender, RoutedEventArgs e)
        {
            int want = sender == EngineGHubRb ? 1 : 0;
            if (want == cfg.Engine) return;
            if (setupBusy || liveBusy) { UpdateEngineUi(); Flash("กำลังทำงานอื่นอยู่ — ลองใหม่อีกครั้ง"); return; }
            var keep = vals?.Select(v => new GHub.GunVals { Base = v.Base, X = v.X, Steps = v.Steps?.ToArray() }).ToArray();
            if (on) ToggleOn();
            await fileWork;
            cfg.Engine = want;
            cfg.Save();
            GHub.Native = Native;
            engine.SetActive(false);
            liveTimer.Interval = TimeSpan.FromMilliseconds(Native ? 60 : 500);
            UpdateEngineUi();
            RefreshGHub();
            Log.Info("engine → " + (Native ? "native" : "G Hub"));
            if (Native)
            {
                // ค่าปืนที่ใช้อยู่ (จาก G Hub) ย้ายมาใช้ในตัวต่อเลย
                if (keep != null) { vals = keep; SaveNative(null); } else LoadGunValues();
                Flash("ใช้ตัวขยับเมาส์ในตัวแล้ว — ไม่ต้องเปิด G Hub");
                return;
            }
            agentPid = GHub.AgentPid();
            if (keep != null && Ask("เอาค่าปืนตอนนี้ (base / x / กราฟ) ใส่สคริปต์ใน G Hub ด้วยไหม?\nต้องรีสตาร์ท G Hub ประมาณ 10 วินาที\n\n(ไม่ = ใช้ค่าที่อยู่ใน G Hub เดิม)"))
            {
                vals = keep;
                applied = Clone(keep);
                await ApplyRestartAsync(keep.Select(v => (v.Steps ?? GHub.DefaultSteps()).ToArray()).ToArray());
            }
            LoadGunValues();
            Flash("กลับไปใช้ G Hub แล้ว — ถ้าสคริปต์ยังไม่พร้อม กด \"ตั้งค่าอัตโนมัติ\" ในหน้าตั้งค่า");
        }

        // ---------------------------------------------------------------- คำนวณ: สโคปของช่องนี้ → ชุดไหน คูณเท่าไหร่
        (int set, double scopeMult, string via, double mult, string parts) Plan(ScopeDetect.Panel p)
        {
            int n = vals?.Length ?? 3;
            int set = p.Set;
            double scopeMult = 1;
            string via = null;
            if (p.Set == 0) set = cfg.ScopeNoneSet;
            else if (p.Set == -2) (set, scopeMult, via) = RecoilCalc.ForScope(p.ScopeLabel, cfg.Names.Take(n).ToArray());
            var calc = cfg.Calc ??= new CalcSettings();
            var (mult, parts) = calc.On ? RecoilCalc.Gear(calc, RecoilCalc.GunKey(p.Name), scopeMult, p.Att, p.AttId) : (1.0, "");
            // ความไวในเกมเปลี่ยนจากตอนจูนตารางของชุดนี้ → ชดเชยให้เอง
            string cardScope = set >= 1 && set <= cfg.Names.Length ? RecoilCalc.SetScope(cfg.Names[set - 1]) : null;
            double sf = calc.On ? RecoilCalc.SensFactor(calc, cardScope) : 1;
            if (Math.Abs(sf - 1) > 0.005) { mult = Math.Clamp(mult * sf, 0.2, 3.9); parts += $" · ความไว ×{sf:0.00}"; }
            // แรงดึงที่เรียนรู้ของสโคปนี้เอง (แยกทุกสโคป)
            double ls = ScopeLearned(p);
            if (Math.Abs(ls - 1) > 0.005) { mult = Math.Clamp(mult * ls, 0.2, 3.9); parts += $" · เรียนรู้สโคป ×{ls:0.00}"; }
            return (set, scopeMult, via, mult, parts);
        }

        // สโคปที่ใช้คำนวณ/เรียนรู้: ศูนย์เหล็ก = "iron", ไฮบริด = เรดดอท, ไม่รู้จัก = null
        static string ScopeKey(ScopeDetect.Panel p) =>
            p == null || p.Set == -1 ? null : p.Set == 0 ? "iron" : p.ScopeLabel == "hybrid" ? "reddot" : p.ScopeLabel;

        // กด Shift (กลั้นหายใจ / ซูมศูนย์ 1x) ของสโคปนี้ คูณแรงดึงเพิ่มเท่าไหร่ (เรียนรู้จากชุดที่กด Shift ตลอด)
        double BreathOf(ScopeDetect.Panel p)
        {
            var c = cfg.Calc;
            string sk = ScopeKey(p);
            return c != null && c.On && c.AutoLearn && sk != null && c.LearnScope.TryGetValue(sk + "+shift", out double v) ? v : 1;
        }

        double HeldBreath() => cfg.ScopeAuto ? BreathOf(slotInfo[Math.Clamp(heldSlot, 1, 2)]) : 1;

        double ScopeLearned(ScopeDetect.Panel p)
        {
            var c = cfg.Calc;
            string sk = ScopeKey(p);
            return c != null && c.On && c.AutoLearn && sk != null && c.LearnScope.TryGetValue(sk, out double v) ? v : 1;
        }

        string HeldScopeText()
        {
            var p = slotInfo[Math.Clamp(heldSlot, 1, 2)];
            if (cfg.ScopeAuto && p != null && p.Set != -1) return p.Set == 0 ? "ศูนย์เหล็ก" : "สโคป " + ScopeName(p.ScopeLabel);
            return "ชุด " + cfg.Names[Math.Clamp(gun, 1, cfg.Names.Length) - 1];
        }

        // ---------------------------------------------------------------- รูปโลโก้ (สร้างจากภาพป้ายของในเกมที่ผู้ใช้แคป เก็บในเครื่อง saved\art)
        //   guns\<ปืน>.png = โลโก้ปืนสีทอง, att\<ชิ้น>.png = ของแต่ง/สโคป — ไม่มีไฟล์ = ใช้ภาพจากหน้ากระเป๋าแทน
        static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> artCache = new();

        static System.Windows.Media.Imaging.BitmapSource ArtOf(string kind, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string key = kind + "/" + id;
            if (artCache.TryGetValue(key, out var bs)) return bs;
            string path = System.IO.Path.Combine(Settings.KitDir, "art", kind, id + ".png");
            try
            {
                if (File.Exists(path))
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                    bs = bi;
                }
            }
            catch (Exception ex) { Log.Error("art " + key, ex); }
            artCache[key] = bs;
            return bs;
        }

        // ---------------------------------------------------------------- หน้ามาโคร: อัปเดตสด
        bool liveDirty;
        DateTime liveAt;
        string liveSig;

        void LiveDirty() => liveDirty = true;

        void LiveTick()
        {
            var now = DateTime.Now;
            if (!liveDirty && now - liveAt < TimeSpan.FromMilliseconds(250)) return;
            liveDirty = false;
            liveAt = now;
            try { UpdateLive(); }
            catch (Exception ex) { Log.Error("live ui", ex); }
        }

        bool spraySeen;

        void UpdateLive()
        {
            var size = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Size;
            string res = $"{size.Width}x{size.Height}";
            ResText.Text = res;
            HeroRes.Text = res;

            GameDot.Fill = gameWas ? PGreen : PRed;
            GamePill.Background = gameWas ? PGreenBg : PRedBg;
            GameText.Text = gameWas ? "TSLGAME ON" : "TSLGAME OFF";
            GameText.Foreground = gameWas ? B("#86EFAC") : B("#FCA5A5");

            var held = cfg.ScopeAuto ? slotInfo[Math.Clamp(heldSlot, 1, 2)] : null;
            HeldText.Text = magSlot == 0 && cfg.HolsterPause ? "ระเบิด / มีด / เก็บปืน"
                          : held != null && held.Name.Length > 0 ? held.Name.ToUpperInvariant()
                          : HeldScopeText();
            HeldSlotText.Text = cfg.ScopeAuto ? $"ช่อง {heldSlot}" : $"ชุด {gun}";

            // สถานะมาโคร
            bool spraying = engine != null && engine.Spraying;
            spraySeen = spraying;
            (Brush dot, Brush bg, Brush fg, string text) st =
                !on ? (PRed, PRedBg, B("#FCA5A5"), cfg.AutoOn ? "มาโครปิด · รอเข้าเกม" : "มาโครปิด")
                : paused ? (PAmber, PAmberBg, B("#FCD34D"), "หยุดชั่วคราว · " + PauseText(pauseWhy))
                : spraying ? (PCyan, PBlueBg, PCyan, "กำลังดึง")
                : (PGreen, PGreenBg, B("#86EFAC"), "พร้อมยิง · " + HeldScopeText() + (magSlot is 1 or 2 && cfg.MagMode != 0 && !IsSemi(magSlot) ? $" · ดึงสูงสุด {MagCap(magSlot) + 2} นัด/ชุด" : ""));
            LiveDot.Fill = st.dot;
            LivePill.Background = st.bg;
            LiveText.Foreground = st.fg;
            LiveText.Text = st.text;

            // ปืน 2 ช่อง — สร้างใหม่เฉพาะตอนข้อมูลเปลี่ยน
            bool any = cfg.ScopeAuto && (slotInfo[1] != null || slotInfo[2] != null);
            HeroPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            SlotGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (!any)
            {
                HeroTitle.Text = cfg.ScopeAuto ? "รอตรวจจับ" : "ตรวจสโคปอัตโนมัติปิดอยู่";
                HeroHint.Text = cfg.ScopeAuto ? " ในเกม — ดูปืน สโคป ของแต่งให้เอง" : " (เปิดได้ในหน้าตั้งค่า)";
                HeroSet.Text = "ตอนนี้ใช้ " + HeldScopeText() + (Native ? " · ตัวขยับเมาส์ในตัว" : " · ผ่าน G Hub");
                liveSig = null;
                return;
            }
            var c = cfg.Calc ??= new CalcSettings();
            string Sig(ScopeDetect.Panel p) => p == null ? "-" :
                $"{p.Name}|{p.Set}|{p.ScopeLabel}|{(p.Att == null ? "" : string.Join("", p.Att.Select(a => a ? 1 : 0)))}|{string.Join(",", p.AttId ?? new string[0])}";
            string sig = $"{string.Join(",", c.LearnN.Select(kv => kv.Key + kv.Value))}#{Sig(slotInfo[1])}#{Sig(slotInfo[2])}#{heldSlot}#{on}#{stance}#{c.On}#{c.MuzzlePct}#{c.GripPct}#{c.StockPct}#{c.CrouchPct}#{c.PronePct}#{c.RefGun}" +
                         $"#{string.Join(",", c.Guns.Select(kv => kv.Key + kv.Value))}#{string.Join(",", cfg.Names)}" +
                         $"#{(vals == null ? "" : string.Join(",", vals.Select(v => v.Base + "/" + (v.Steps?.Length ?? 0))))}";
            if (sig == liveSig) return;
            liveSig = sig;
            SlotHost1.Child = SlotPanel(1);
            SlotHost2.Child = SlotPanel(2);
        }

        static string PauseText(string why) => why switch
        {
            "mag empty" => "แม็กหมด กด R",
            "grenade / melee / holstered" => "ถือระเบิด/มีด",
            "game not in front" => "ไม่ได้อยู่ในเกม",
            "cursor shown (inventory/map/menu)" => "เปิดกระเป๋า/แผนที่",
            _ => why ?? ""
        };

        static string GunClass(string key) => key switch
        {
            "UMP" or "VECTOR" or "MICRO" or "UZI" or "TOMMY" or "MP5K" or "PP19" or "BIZON" or "P90" or "JS9" or "MP9" => "SMG",
            "DP28" or "M249" or "MG3" or "RPD" => "LMG",
            "MINI14" or "SKS" or "SLR" or "MK12" or "QBU" or "VSS" or "MK14" or "DRAGUNOV" => "DMR",
            "KAR98K" or "M24" or "AWM" or "MOSIN" or "LYNX" or "WIN94" => "SNIPER",
            null => "ปืน",
            _ => "RIFLE"
        };

        static string Pct(double mult) =>
            Math.Abs(mult - 1) < 0.005 ? "0%" : (mult > 1 ? "+" : "−") + Math.Abs((mult - 1) * 100).ToString("0") + "%";

        static TextBlock T(string text, double size, Brush fg, FontWeight? w = null, FontFamily f = null) =>
            new TextBlock { Text = text, FontSize = size, Foreground = fg, FontWeight = w ?? FontWeights.Normal, FontFamily = f ?? new FontFamily("Segoe UI, Leelawadee UI"), VerticalAlignment = VerticalAlignment.Center };

        static Border Chip(string text, Brush fg, Brush bg) => new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 1, 7, 2), Background = bg, VerticalAlignment = VerticalAlignment.Center,
            Child = T(text, 10, fg, FontWeights.Bold)
        };

        Button MiniBtn(string glyph, Action click)
        {
            var b = new Button { Style = (Style)FindResource("StepBtn"), Content = glyph, Margin = new Thickness(4, 0, 0, 0) };
            b.Click += (s, e) => click();
            return b;
        }

        // ---------------------------------------------------------------- สอนสโคป
        //   สโคปที่ยังไม่รู้จัก (2x, 3x, Holo, 8x, 15x, สโคปมีสกิน …) หรืออ่านผิด → กดเลือกครั้งเดียว จำภาพช่องสโคปนั้นไว้ (saved\\scopes)
        static readonly (string label, string text)[] ScopeChoices =
            { ("reddot", "Red Dot"), ("holo", "Holo"), ("2x", "2x"), ("3x", "3x"), ("4x", "4x"), ("6x", "6x"), ("8x", "8x"), ("15x", "15x"), ("hybrid", "Hybrid") };
        readonly bool[] teachOpen = new bool[3];

        static string ScopeName(string label) => ScopeChoices.FirstOrDefault(c => c.label == label).text ?? label;

        Border Pill(string text, Brush fg, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 0, 6, 6),
                Background = PInner, BorderBrush = PLine, BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
                Child = T(text, 11.5, fg, FontWeights.Bold)
            };
            b.MouseEnter += (s, e) => b.BorderBrush = PBlue;
            b.MouseLeave += (s, e) => b.BorderBrush = PLine;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        // ---------------------------------------------------------------- สอนของแต่ง (อ่านไม่ได้/อ่านผิด → กดเลือกครั้งเดียว จำภาพช่องจริงไว้)
        readonly bool[,] attTeachOpen = new bool[3, 4];

        UIElement AttTeachRow(int s, int slot, ScopeDetect.Panel p)
        {
            var wrap = new WrapPanel { Margin = new Thickness(2, -6, 0, 8) };
            if (!attTeachOpen[s, slot])
            {
                wrap.Children.Add(Pill("ผิด? แก้", PFaint, () => { attTeachOpen[s, slot] = true; liveSig = null; LiveDirty(); }));
                return wrap;
            }
            string cat = slot == 0 ? "muzzle" : slot == 1 ? "grip" : "stock";
            wrap.Children.Add(new TextBlock { Text = "ติดอยู่คือ:", FontSize = 11.5, Foreground = PMuted, Margin = new Thickness(0, 4, 8, 6) });
            foreach (var it in AttachmentId.Items.Where(i => i.Slot == cat))
                wrap.Children.Add(Pill(it.Name, it.Id == p.AttId?[slot] ? PCyan : PText, () => TeachAtt(s, slot, it.Id)));
            wrap.Children.Add(Pill("ไม่ได้ติด", PText, () => TeachAtt(s, slot, null)));
            wrap.Children.Add(Pill("ยกเลิก", PFaint, () => { attTeachOpen[s, slot] = false; liveSig = null; LiveDirty(); }));
            return wrap;
        }

        void TeachAtt(int s, int slot, string id)
        {
            var p = slotInfo[s];
            if (p?.Att == null) return;
            var icon = p.AttIcons?[slot];
            if (id != null)
            {
                if (icon == null || ScopeDetect.Obscured(icon) || RecoilCalc.Sharpness(icon) < 12 || !AttachmentId.Teach(icon, id))
                {
                    PopOsd("ภาพช่องของแต่งไม่ชัด — เปิดกระเป๋าใหม่โดยไม่ชี้เมาส์ที่ช่องนั้น แล้วกดเลือกอีกครั้ง");
                    return;
                }
            }
            p.Att[slot] = id != null;
            if (p.AttId != null) p.AttId[slot] = id;
            attTeachOpen[s, slot] = false;
            liveSig = null;
            ApplyScope("taught attachment");
            LiveDirty();
            PopOsd(id == null ? "ตั้งเป็น: ไม่ได้ติด" : $"จำแล้ว: {AttachmentId.Get(id)?.Name}");
        }

        UIElement ScopeTeachRow(int s, ScopeDetect.Panel p)
        {
            var wrap = new WrapPanel { Margin = new Thickness(2, -4, 0, 8) };
            if (p.Set != -1 && !teachOpen[s])
            {
                wrap.Children.Add(Pill("สโคปผิด? แก้", PFaint, () => { teachOpen[s] = true; liveSig = null; LiveDirty(); }));
                return wrap;
            }
            wrap.Children.Add(new TextBlock { Text = "สโคปนี้คือ:", FontSize = 11.5, Foreground = PMuted, Margin = new Thickness(0, 4, 8, 6) });
            foreach (var (label, text) in ScopeChoices)
                wrap.Children.Add(Pill(text, label == p.ScopeLabel ? PCyan : PText, () => TeachScope(s, label)));
            if (p.Set != -1) wrap.Children.Add(Pill("ยกเลิก", PFaint, () => { teachOpen[s] = false; liveSig = null; LiveDirty(); }));
            return wrap;
        }

        void TeachScope(int s, string label)
        {
            var p = slotInfo[s];
            if (p?.Icon == null) return;
            // ภาพช่องสโคปต้องเห็นไอคอนชัด (ไม่โดนเมาส์ชี้/ป้ายบัง) ไม่งั้นจำภาพผิด
            if (ScopeDetect.Obscured(p.Icon) || ScopeDetect.BandSd(p.Icon, 0.06, 0.40) < 12)
            {
                PopOsd("ภาพช่องสโคปไม่ชัด — เปิดกระเป๋าใหม่โดยไม่ชี้เมาส์ที่ช่องสโคป แล้วกดเลือกอีกครั้ง");
                return;
            }
            if (label == "hybrid") label = "reddot";        // ไฮบริดใช้ค่าเดียวกับเรดดอท
            int set = ScopeDetect.SetFor(label, cfg.Names);
            ScopeDetect.Teach(p.Icon, set, label);
            p.Set = set;
            p.ScopeLabel = label;
            p.Score = 1;
            teachOpen[s] = false;
            liveSig = null;
            ApplyScope("taught");
            LiveDirty();
            PopOsd($"จำแล้ว: สโคปนี้ = {ScopeName(label)}");
        }

        UIElement SlotPanel(int s)
        {
            var p = slotInfo[s];
            var root = new StackPanel();
            bool isHeld = heldSlot == s;
            string gk = p == null ? null : RecoilCalc.GunKey(p.Name);

            // หัว: ชื่อปืน + สถานะ
            var head = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
            var state = isHeld ? Chip(on ? "● ใช้อยู่" : "● ถืออยู่", B("#86EFAC"), PGreenBg) : Chip("● สแตนด์บาย", PMuted, PInner);
            DockPanel.SetDock(state, Dock.Right);
            head.Children.Add(state);
            var title = new StackPanel();
            var name = T(p == null || p.Name.Length == 0 ? "—" : p.Name.ToUpperInvariant(), 30, p == null ? PFaint : PText, FontWeights.Bold, Big);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Children.Add(name);
            title.Children.Add(T($"{GunClass(gk)}  ·  SLOT 0{s}", 10.5, PFaint, FontWeights.Bold));
            head.Children.Add(title);
            root.Children.Add(head);

            if (p == null || p.Slot == 0)
            {
                var empty = new Border
                {
                    CornerRadius = new CornerRadius(14), BorderBrush = PLine, BorderThickness = new Thickness(1), Height = 230,
                    Background = new VisualBrush
                    {
                        TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 12, 12), ViewportUnits = BrushMappingMode.Absolute,
                        Visual = new Line { X1 = 0, Y1 = 12, X2 = 12, Y2 = 0, Stroke = B("#121B30"), StrokeThickness = 1 }
                    }
                };
                var es = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                es.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 26, Foreground = PFaint, HorizontalAlignment = HorizontalAlignment.Center });
                es.Children.Add(T("ช่องว่าง", 12, PFaint, FontWeights.Bold));
                ((TextBlock)es.Children[1]).HorizontalAlignment = HorizontalAlignment.Center;
                empty.Child = es;
                root.Children.Add(empty);
                return new Border { Opacity = 0.75, Child = root };
            }

            var c = cfg.Calc ??= new CalcSettings();
            var (set, scopeMult, via, mult, _) = Plan(p);
            bool setOk = set >= 1 && vals != null && set <= vals.Length;
            string setName = setOk ? cfg.Names[set - 1] : "ชุดเดิม";

            // สโคป
            string scopeTitle = p.Set == 0 ? "ไม่มีสโคป (ศูนย์เหล็ก)" : p.Set == -1 ? "ยังไม่รู้จัก — เลือกด้านล่างครั้งเดียว จำให้เอง" : ScopeName(p.ScopeLabel);
            double scopeAll = scopeMult * ScopeLearned(p);
            string scopeNm = p.Set == 0 ? "ศูนย์เหล็ก" : ScopeName(p.ScopeLabel);
            string scopeSub = p.Set == -1 ? "ยังใช้ค่าเดิมไปก่อน" : !setOk ? "ใช้ค่าที่เลือกอยู่"
                : $"แรงดึงของ {scopeNm} คำนวณจากความไว {scopeNm} ในเกม" + (Math.Abs(ScopeLearned(p) - 1) > 0.005 ? $" · เรียนรู้แล้ว ×{ScopeLearned(p):0.00}" : "")
                  + (Math.Abs(BreathOf(p) - 1) > 0.005 ? $" · กด Shift ×{BreathOf(p):0.00}" : "");
            root.Children.Add(Row("", "สโคป", scopeTitle, scopeSub, scopeAll, PBlue, null, null, p.Set == -1, p.Set > 0 || p.Set == -2 ? ArtOf("att", ScopeKey(p) == "reddot" && p.ScopeLabel == "hybrid" ? "hybrid" : p.ScopeLabel) : null));
            root.Children.Add(ScopeTeachRow(s, p));

            // แรงดึง: ตัวคูณรวม | กราฟ | ท่าทาง
            var mid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            mid.ColumnDefinitions.Add(new ColumnDefinition());
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            var left = new StackPanel();
            left.Children.Add(Stat("ตัวคูณ", "×" + mult.ToString("0.00"), Pct(mult)));
            left.Children.Add(Stat("สโคป", p.Set == -1 ? "?" : scopeNm, Pct(scopeAll)));
            mid.Children.Add(left);
            var graph = Graph(setOk ? vals[set - 1] : null, mult, gk, ArtOf("guns", gk) ?? PicOf(p), gk != null && c.Learn.TryGetValue(gk, out var lc) ? lc : null);
            Grid.SetColumn(graph, 1);
            mid.Children.Add(graph);
            var right = new StackPanel();
            right.Children.Add(StanceBox("", "ย่อ", c.CrouchPct, isHeld && stance == 1));
            right.Children.Add(StanceBox("", "หมอบ", c.PronePct, isHeld && stance == 2));
            Grid.SetColumn(right, 2);
            mid.Children.Add(right);
            root.Children.Add(mid);

            // ปืน
            double gf = RecoilCalc.GunFactor(c, gk);
            string magTxt = gk != null && RecoilCalc.GunInfo.TryGetValue(gk, out var gInfo)
                ? $" · แม็ก {(p.ExtMag ? gInfo.ext + " (ขยาย)" : gInfo.mag.ToString())} นัด" : "";
            if (gk != null && RecoilCalc.SemiKick.ContainsKey(gk)) magTxt += " · ปืนเคาะ: ดึงทีละนัด";
            else if (magTxt != "") magTxt += $" → ดึงสูงสุด {MagCap(s) + 2} นัด/ชุด";
            if (p.Att != null && p.Att[2] && !p.ExtMag) magTxt += " · แม็ก QuickDraw";
            string learnTxt = gk != null && c.LearnN.TryGetValue(gk, out int ln) ? $" · เรียนรู้แล้ว {ln} ชุด" : " · ยังไม่ได้เรียนรู้ (ยิงค้างตอนเล็งแล้วปรับเอง)";
            root.Children.Add(Row("", "ปืน", gk ?? "ไม่รู้จักชื่อปืน", gk == null ? "ใช้ ×1" : $"แรงดีดเทียบ {c.RefGun ?? "AUG"}{(c.Guns.TryGetValue(gk, out double u) && Math.Abs(u - 1) > 0.001 ? $" × ปรับ {u:0.00}" : "")}{magTxt}{learnTxt}",
                                  gf, PGold, gk == null ? null : () => AdjustGun(gk, -0.05), gk == null ? null : () => AdjustGun(gk, +0.05), gk == null));

            // ของแต่ง
            int n = p.Att == null ? 0 : (p.Att[0] ? 1 : 0) + (p.Att[1] ? 1 : 0) + (p.Att[3] ? 1 : 0);
            var ah = new DockPanel { Margin = new Thickness(2, 4, 2, 8) };
            var cnt = Chip(n.ToString(), PCyan, PBlueBg);
            cnt.Margin = new Thickness(8, 0, 0, 0);
            ah.Children.Add(T("ของแต่งที่ติด", 10.5, PMuted, FontWeights.Bold));
            ah.Children.Add(cnt);
            root.Children.Add(ah);
            if (p.Att == null)
                root.Children.Add(T("  อ่านของแต่งไม่ได้รอบนี้ — เปิดกระเป๋าอีกครั้ง", 11, PFaint));
            else
            {
                foreach (var (k, nm) in new[] { (0, "ปากกระบอก"), (1, "กริป"), (3, "พานท้าย") })
                {
                    root.Children.Add(AttRow(k, nm, p, c));
                    root.Children.Add(AttTeachRow(s, k, p));
                }
            }
            if (!c.On) root.Children.Add(T("  คำนวณแรงดีดปิดอยู่ — ใช้ตารางในชุดค่าตรงๆ", 11, PAmber));
            return root;
        }

        void AdjustGun(string gk, double d)
        {
            var c = cfg.Calc;
            double u = c.Guns.TryGetValue(gk, out double v) ? v : 1;
            c.Guns[gk] = Math.Round(Math.Clamp(u + d, 0.3, 3), 2);
            cfg.Save();
            CalcChanged();
            LiveDirty();
        }

        Border Row(string glyph, string tag, string title, string sub, double mult, Brush accentBrush, Action minus, Action plus, bool dim,
                   System.Windows.Media.Imaging.BitmapSource icon = null)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = PInner, BorderBrush = PLine, BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = icon != null
                    ? new System.Windows.Controls.Image { Source = icon, Stretch = Stretch.Uniform, Margin = new Thickness(3) }
                    : new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17, Foreground = accentBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(Chip(tag, accentBrush, PInner));
            var tt = T(title, 13, PText, FontWeights.SemiBold);
            tt.Margin = new Thickness(8, 0, 0, 0);
            tt.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(tt);
            mid.Children.Add(top);
            var bottom = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
            var pct = T(Pct(mult), 14, mult > 1.005 ? B("#FB923C") : mult < 0.995 ? B("#4ADE80") : PMuted, FontWeights.Bold, Mono);
            pct.Margin = new Thickness(0, 0, 10, 0);
            bottom.Children.Add(pct);
            var st = T(sub, 10.5, PFaint);
            st.TextTrimming = TextTrimming.CharacterEllipsis;
            bottom.Children.Add(st);
            mid.Children.Add(bottom);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);
            if (minus != null)
            {
                var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                btns.Children.Add(MiniBtn("", minus));
                btns.Children.Add(MiniBtn("", plus));
                Grid.SetColumn(btns, 2);
                g.Children.Add(btns);
            }
            return new Border
            {
                CornerRadius = new CornerRadius(14), Background = PCard, BorderBrush = dim ? PLine : accentBrush, BorderThickness = new Thickness(dim ? 1 : 0, 1, 1, 1),
                Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 10), Child = g, Opacity = dim ? 0.7 : 1
            };
        }

        Border AttRow(int slot, string name, ScopeDetect.Panel p, CalcSettings c)
        {
            bool has = p.Att[slot];
            string id = p.AttId?[slot];
            var it = AttachmentId.Get(id);
            double def = RecoilCalc.DefPct(c, slot);
            bool refHas = slot == 0 ? c.RefMuzzle : slot == 1 ? c.RefGrip : c.RefStock;
            string refId = slot == 0 ? c.RefMuzzleId : slot == 1 ? c.RefGripId : c.RefStockId;
            double rel = RecoilCalc.AttFactor(has, id, def) / RecoilCalc.AttFactor(refHas, refId, def);
            string title = !has ? "ไม่ได้ติด" : it != null ? it.Name : "ติดอยู่ (ไม่แน่ใจชนิด)";
            string sub = !has ? "ไม่มีผล" : it != null ? $"คุมแรงดีดแนวตั้ง +{it.Vertical:0}% (ตัวเลขจากเกม)" : $"ใช้ค่ากลาง {def:+0;-0}%";
            if (refHas) sub += $" · ตารางจูนตอนติด{AttachmentId.Get(refId)?.Name ?? "ไว้"}";
            void Step(double d)
            {
                if (slot == 0) c.MuzzlePct = Math.Clamp(c.MuzzlePct + d, -60, 30);
                else if (slot == 1) c.GripPct = Math.Clamp(c.GripPct + d, -60, 30);
                else c.StockPct = Math.Clamp(c.StockPct + d, -60, 30);
                cfg.Save(); CalcChanged(); LiveDirty();
            }
            bool tune = has && it == null;          // รู้ชนิดแล้วใช้ตัวเลขเกม ไม่ต้องปรับ
            var r = Row(has ? "\uE73E" : "\uE711", name, title, sub, rel, has ? PGreen : PFaint,
                        tune ? () => Step(-1) : null, tune ? () => Step(+1) : null, !has, has ? ArtOf("att", id) : null);
            r.Margin = new Thickness(0, 0, 0, 8);
            return r;
        }

        Border Stat(string label, string value, string sub) => new Border
        {
            CornerRadius = new CornerRadius(12), Background = PCard, BorderBrush = PLine, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(6, 7, 6, 7),
            Child = new StackPanel
            {
                Children =
                {
                    Center(T(label, 9.5, PFaint, FontWeights.Bold)),
                    Center(T(value, 14, PText, FontWeights.Bold, Mono)),
                    Center(T(sub, 9.5, PMuted)),
                }
            }
        };

        static TextBlock Center(TextBlock t) { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextTrimming = TextTrimming.CharacterEllipsis; return t; }

        Border StanceBox(string glyph, string label, double pct, bool now) => new Border
        {
            CornerRadius = new CornerRadius(12), Background = now ? PBlueBg : PCard, BorderBrush = now ? PCyan : PLine, BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 0, 0, 8), Padding = new Thickness(6, 7, 6, 7),
            Child = new StackPanel
            {
                Children =
                {
                    Center(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Foreground = now ? PCyan : PMuted }),
                    Center(T(label, 10, PMuted, FontWeights.Bold)),
                    Center(T((pct > 0 ? "+" : "−") + Math.Abs(pct).ToString("0") + "%", 12.5, now ? PCyan : PText, FontWeights.Bold, Mono)),
                }
            }
        };

        // กราฟแรงดึงของชุดค่า (× ตัวคูณ) 3 วินาทีแรก
        static System.Windows.Media.Imaging.BitmapSource PicOf(ScopeDetect.Panel p)
        {
            if (p?.PicUi is System.Windows.Media.Imaging.BitmapSource done) return done;
            if (p?.Pic == null) return null;
            try
            {
                using var ms = new MemoryStream();
                p.Pic.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                p.PicUi = bi;
                return bi;
            }
            catch { return null; }
        }

        Border Graph(GHub.GunVals v, double mult, string gk, System.Windows.Media.Imaging.BitmapSource pic, double[] learned)
        {
            var g = new Grid();
            if (pic != null)
                g.Children.Add(new Image
                {
                    Source = pic, Stretch = Stretch.Uniform, Margin = new Thickness(10, 22, 10, 30), Opacity = 0.95,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xC9, 0xA5, 0x4A), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 }
                });
            var cap = new DockPanel { Margin = new Thickness(10, 7, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            var r = T(gk == null ? "" : "MUL " + mult.ToString("0.00"), 9, PFaint, FontWeights.Bold, Mono);
            DockPanel.SetDock(r, Dock.Right);
            cap.Children.Add(r);
            var dotRow = new StackPanel { Orientation = Orientation.Horizontal };
            dotRow.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = PGreen, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            dotRow.Children.Add(T("RECOIL_CTRL // " + (Native ? "NATIVE" : "G HUB"), 9, PFaint, FontWeights.Bold, Mono));
            cap.Children.Add(dotRow);
            g.Children.Add(cap);
            if (v?.Steps != null && v.Steps.Length > 0)
            {
                int n = Math.Min(300, v.Steps.Length);
                var ys = Enumerable.Range(0, n).Select(i => (v.Steps[i] + v.Base) * mult).ToArray();
                double max = Math.Max(1e-6, ys.Max());
                var pts = new PointCollection(ys.Select((y, i) => new Point(i, 60 - y / max * 52)));
                var fill = new PointCollection(pts) { new Point(n - 1, 60), new Point(0, 60) };
                var canvas = new Canvas { Width = n, Height = 60 };
                canvas.Children.Add(new Polygon
                {
                    Points = fill,
                    Fill = new LinearGradientBrush(Color.FromArgb(0x66, 0x1D, 0x6B, 0xFF), Color.FromArgb(0x00, 0x1D, 0x6B, 0xFF), 90)
                });
                canvas.Children.Add(new Polyline
                {
                    Points = pts, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round,
                    Stroke = new LinearGradientBrush(Color.FromRgb(0x1D, 0x6B, 0xFF), Color.FromRgb(0x5C, 0xE1, 0xFF), 0)
                });
                g.Children.Add(new Viewbox
                {
                    Stretch = Stretch.Fill, Child = canvas, Opacity = pic != null ? 0.55 : 1,
                    Margin = pic != null ? new Thickness(10, 0, 10, 20) : new Thickness(10, 26, 10, 22),
                    Height = pic != null ? 34 : double.NaN, VerticalAlignment = pic != null ? VerticalAlignment.Bottom : VerticalAlignment.Stretch
                });
            }
            if (learned != null)
            {
                var lt = T("LEARN " + string.Join(" ", learned.Take(6).Select(x => x.ToString("0.00"))), 8.5, PCyan, FontWeights.Bold, Mono);
                lt.HorizontalAlignment = HorizontalAlignment.Left;
                lt.VerticalAlignment = VerticalAlignment.Bottom;
                lt.Margin = new Thickness(10, 0, 0, 6);
                g.Children.Add(lt);
            }
            var ready = T(on && !paused ? "[ SPRAY READY ]" : "[ STANDBY ]", 9, on && !paused ? PGreen : PFaint, FontWeights.Bold, Mono);
            ready.HorizontalAlignment = HorizontalAlignment.Right;
            ready.VerticalAlignment = VerticalAlignment.Bottom;
            ready.Margin = new Thickness(0, 0, 10, 6);
            g.Children.Add(ready);
            return new Border
            {
                CornerRadius = new CornerRadius(12), Background = PInner, BorderBrush = PLine, BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8), Child = g, MinHeight = 120
            };
        }

        // ---------------------------------------------------------------- ปุ่มลัดแรงดีด
        void RefreshKeys_Click(object sender, RoutedEventArgs e)
        {
            pubgSlotKeys = null;
            stanceKeys = null;
            BuildKeyCards();
            Flash("อ่านปุ่มจากไฟล์ตั้งค่า PUBG ใหม่แล้ว");
        }

        void BuildKeyCards()
        {
            stanceKeys ??= ReadStanceKeys();
            var sk = SlotKeys;
            var gs = GameSettings.Current;
            KeyCards.Children.Clear();
            KeyCards.Children.Add(KeyCard("", "เริ่มดึง", "ใช้ชุดตามสโคปของปืนนั้น", PGreen, new[]
            {
                ("ปืนช่อง 1", "ปืนหลัก: ดึงแรงดีด", KeyName(sk[0])),
                ("ปืนช่อง 2", "ปืนรอง: ดึงแรงดีด", KeyName(sk[1])),
                ("เปิด/ปิดมาโคร", cfg.AutoOn ? "เปิดเองตอนเข้าเกม" : "กดเอง", KeyName(cfg.HotKey)),
            }));
            KeyCards.Children.Add(KeyCard("", "หยุดดึง", "ไม่ดึงชั่วคราว", PRed, new[]
            {
                ("ปืนพก / มีด / ระเบิด", cfg.HolsterPause ? "ถืออยู่ = ไม่ดึง" : "ปิดอยู่ (ดึงตลอด)", $"{KeyName(gs.Pistol)} · {KeyName(gs.Melee)} · {KeyName(gs.Throwable)}"),
                ("เก็บปืน", cfg.HolsterPause ? "ไม่ดึงจนกว่าจะหยิบปืน" : "ปิดอยู่", KeyName(gs.Holster)),
                ("กระเป๋า / แผนที่", "เคอร์เซอร์โผล่ = ไม่ดึง", "Tab · M"),
                ("แม็ก", cfg.MagMode != 0 ? "แต่ละชุดดึงไม่เกินขนาดแม็กของปืน (+แม็กขยาย) + 2 นัด" : "ไม่จำกัด (เทรนนิ่ง)", "อัตโนมัติ"),
            }));
            KeyCards.Children.Add(KeyCard("", "ใช้แรงดึง", "ตอนยิง", PBlue, new[]
            {
                ("ยิง", "กดค้างเพื่อดึง", "คลิกซ้าย"),
                ("เล็ง (ADS)", (cfg.RequireAds ? "ต้องกดค้างถึงดึง" : "ไม่บังคับ") + " · เกม: " + (gs.AdsMode == "Toggle" ? "กดสลับ" : "กดค้าง"), "คลิกขวา"),
                ("ย่อ / หมอบ", "ลดแรงดึงตามท่า", KeyName(stanceKeys[0]) + " · " + KeyName(stanceKeys[1])),
            }));
        }

        Border KeyCard(string glyph, string title, string sub, Brush color, (string name, string desc, string key)[] rows)
        {
            var sp = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var chip = Chip(sub, color, PInner);
            DockPanel.SetDock(chip, Dock.Right);
            head.Children.Add(chip);
            var hs = new StackPanel { Orientation = Orientation.Horizontal };
            hs.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Foreground = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            hs.Children.Add(T(title, 13, PText, FontWeights.Bold));
            head.Children.Add(hs);
            sp.Children.Add(head);
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) sp.Children.Add(new Border { Height = 1, Background = PLine, Margin = new Thickness(0, 6, 0, 6) });
                var row = new DockPanel();
                var k = new Border
                {
                    CornerRadius = new CornerRadius(7), BorderBrush = B("#24345A"), BorderThickness = new Thickness(1), Background = B("#121B30"),
                    Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center, Child = T(rows[i].key, 11, PText, FontWeights.Bold)
                };
                DockPanel.SetDock(k, Dock.Right);
                row.Children.Add(k);
                var l = new StackPanel();
                l.Children.Add(T(rows[i].name, 12, PText, FontWeights.SemiBold));
                l.Children.Add(T(rows[i].desc, 10.5, PFaint));
                row.Children.Add(l);
                sp.Children.Add(row);
            }
            return new Border
            {
                CornerRadius = new CornerRadius(14), Background = PInner, BorderBrush = PLine, BorderThickness = new Thickness(1, 3, 1, 1),
                Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 0), Child = sp
            }.Also(b => b.BorderBrush = new LinearGradientBrush(((SolidColorBrush)color).Color, ((SolidColorBrush)PLine).Color, new Point(0, 0), new Point(0, 0.08)));
        }

        // ---------------------------------------------------------------- เก็บของเร็ว
        void BuildLootSteps()
        {
            (string t, string d)[] steps =
            {
                ("เข้าใกล้ของ", "ยืนใกล้ของบนพื้น หรือกล่องศพ"),
                ("เปิดกระเป๋า + กดปุ่ม", "Tab แล้วกดปุ่มเก็บของที่ตั้งไว้ครั้งเดียว"),
                ("เก็บให้อัตโนมัติ", "ลากของเข้ากระเป๋าทีละช่อง แล้วเมาส์กลับกลางจอ"),
            };
            LootSteps.Children.Clear();
            for (int i = 0; i < steps.Length; i++)
            {
                var g = new DockPanel();
                var num = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = PBlueBg, BorderBrush = PBlue, BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0),
                    Child = Center(T((i + 1).ToString(), 12, PCyan, FontWeights.Bold))
                };
                g.Children.Add(num);
                var sp = new StackPanel();
                sp.Children.Add(T(steps[i].t, 12.5, PText, FontWeights.Bold));
                var d = T(steps[i].d, 11, PFaint);
                d.TextWrapping = TextWrapping.Wrap;
                sp.Children.Add(d);
                g.Children.Add(sp);
                LootSteps.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12), Background = PInner, BorderBrush = PLine, BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 0), Child = g
                });
            }
        }

        bool lootUi;

        void UpdateLootPage()
        {
            if (LootSwitch == null) return;
            lootUi = true;
            bool cap = capturing && captureFor == 6;
            LootSwitch.IsChecked = cfg.LootKey != 0 || cap;
            LootKeyBtn.Content = cap ? "กดปุ่ม / ปุ่มเมาส์... (Esc = ยกเลิก)" : cfg.LootKey == 0 ? "ตั้งปุ่ม" : "ปุ่ม: " + KeyName(cfg.LootKey);
            if (cap) LootKeyBtn.BorderBrush = accent; else LootKeyBtn.ClearValue(BorderBrushProperty);
            LootSlotsText.Text = Math.Clamp(cfg.LootSlots, 1, 15).ToString();
            LootSpeed0.IsChecked = cfg.LootSpeed == 0;
            LootSpeed1.IsChecked = cfg.LootSpeed == 1;
            LootSpeed2.IsChecked = cfg.LootSpeed == 2;
            LootPosText.Text = File.Exists(LootWindow.RefImagePath) ? "ตั้งจากภาพในเกมแล้ว" : "ยังใช้ตำแหน่งเริ่มต้น — กด \"ตั้งตำแหน่ง\" ให้ตรงจอคุณ";
            lootUi = false;
        }

        void LootSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (LootSwitch.IsChecked == true)
            {
                if (cfg.LootKey == 0) BeginCapture(6);
            }
            else
            {
                if (capturing && captureFor == 6) capturing = false;
                cfg.LootKey = 0;
                cfg.Save();
                Flash("ปิดเก็บของเร็ว");
            }
            UpdateLootPage();
        }

        void LootKey_Click(object sender, RoutedEventArgs e) => BeginCapture(6);

        void LootSlots_Click(object sender, RoutedEventArgs e)
        {
            int d = (string)((Button)sender).Tag == "+" ? 1 : -1;
            cfg.LootSlots = Math.Clamp(cfg.LootSlots + d, 1, 15);
            cfg.Save();
            UpdateLootPage();
        }

        void LootSpeed_Checked(object sender, RoutedEventArgs e)
        {
            if (lootUi) return;
            cfg.LootSpeed = int.Parse((string)((FrameworkElement)sender).Tag);
            cfg.Save();
        }
    }

    public partial class MainWindow
    {
        // ---------------------------------------------------------------- ตั้งค่าเกม PUBG (อ่านเองจากไฟล์ตั้งค่าเกม)
        // ---------------------------------------------------------------- เรียนรู้แรงดีดจากมือผู้ใช้ (ไม่ต้องจูน)
        //   ระหว่างมาโครดึง ถ้ามือยังดึงลงช่วย = มาโครดึงน้อยไป, ถ้ามือดันขึ้น = ดึงเกิน → ปรับปืนนั้นทีละนิด แยกตามช่วงเวลา
        //   นับเฉพาะเมาส์ตัวที่กดยิง (การขยับของมาโครมาจากอุปกรณ์ 0) · เฉพาะตอนเล็ง ยิงค้าง ≥ 0.9 วิ และไม่ได้ลากซ้ายขวาเยอะ (ไม่ได้ไล่เป้า)
        readonly double[] learnY = new double[NativeRecoil.BINS];
        double learnX, learnDx;                   // |ซ้ายขวา| รวม (ไว้กรองตอนไล่เป้า), ซ้ายขวาแบบมีทิศ (ไว้เรียนรู้การไหล)
        int learnId = -1;
        IntPtr fireDev;
        System.Windows.Threading.DispatcherTimer learnSave;

        string HeldGunKey() => cfg.ScopeAuto && slotInfo[Math.Clamp(heldSlot, 1, 2)] is ScopeDetect.Panel p ? RecoilCalc.GunKey(p.Name) : null;

        double[] HeldCurve()
        {
            var c = cfg.Calc;
            string gk = HeldGunKey();
            return c != null && c.AutoLearn && gk != null && c.Learn.TryGetValue(gk, out var cv) && cv?.Length == NativeRecoil.BINS ? cv : null;
        }

        void LearnInput(IntPtr dev, int dx, int dy, int left)
        {
            if (left > 0) fireDev = dev;
            if (dev != IntPtr.Zero && (dx != 0 || dy != 0)) ViewTracker.Shared.Hand(dx, dy);   // เมาส์จริง (ไว้หา px ต่อ count)
            if (engine == null || dev == IntPtr.Zero || dev != fireDev) return;
            // ปืนเคาะ: นับมือในช่วง 0.4 วิหลังเตะ
            double sinceKick = (System.Diagnostics.Stopwatch.GetTimestamp() - engine.KickTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (engine.Semi && sinceKick < KICK_WINDOW_MS)
            {
                if (engine.SprayId != kickId) { kickId = engine.SprayId; kickUserY = 0; kickUserX = 0; }
                kickUserY += dy;
                kickUserX += Math.Abs(dx);
                return;
            }
            if (!engine.Spraying) return;
            int id = engine.SprayId;
            if (id != learnId) { learnId = id; Array.Clear(learnY); learnX = 0; learnDx = 0; }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - engine.SprayStartTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            learnY[Math.Clamp((int)(ms / NativeRecoil.BIN_MS), 0, NativeRecoil.BINS - 1)] += dy;
            learnX += Math.Abs(dx);
            learnDx += dx;
        }

        double HeldXBias()
        {
            var c = cfg.Calc;
            string gk = HeldGunKey();
            return c != null && c.AutoLearn && gk != null && c.LearnX.TryGetValue(gk, out double x) ? x : 0;
        }

        void LearnSpray(double[] macroY, double ms, double ads, int id)
        {
            var c = cfg.Calc;
            string gk = HeldGunKey();
            if (c == null || !c.AutoLearn || gk == null || !Native) return;
            if (ms < 900 || ads < 0.8 || paused) return;
            double total = macroY.Sum();
            if (total < 40) return;
            double[] user = id == learnId ? learnY.ToArray() : new double[NativeRecoil.BINS];
            string sk = ScopeKey(slotInfo[Math.Clamp(heldSlot, 1, 2)]);
            // วัดจากภาพ: มุมกล้องที่ขยับจริงแต่ละช่วง → แรงที่ยังขาด(+)/เกิน(−) = มือ − ภาพ ÷ k (count)
            //   ไม่ต้องพึ่งว่าผู้ใช้แก้มือพอดีไหม และใช้ได้ทุกค่า sens (k วัดจากเกมจริงแยกสโคป)
            var vt = ViewTracker.Shared;
            double shf = engine.LastShiftFrac;
            bool breathSpray = shf >= 0.8, mixed = shf > 0.2 && shf < 0.8;
            string kKey = sk == null ? null : sk + (breathSpray ? "+shift" : "");
            double ky = mixed ? 0 : vt.KY(kKey), kxv = mixed ? 0 : vt.KX(kKey);
            double[] need = user;
            double visX = double.NaN;
            bool vision = ky > 0 && engine.SprayId == id;
            if (vision)
            {
                long st = engine.SprayStartTs, binT = (long)(NativeRecoil.BIN_MS / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
                need = new double[NativeRecoil.BINS];
                for (int b = 0; b < NativeRecoil.BINS && vision; b++)
                {
                    var (_, vy, fr) = vt.Sum(st + b * binT, st + (b + 1) * binT);
                    if (macroY[b] >= 15 && fr < 20) vision = false;           // วัดภาพได้ไม่พอ (ฉากมืด/ควัน) → ใช้มือแทน
                    need[b] = user[b] - vy / ky;
                }
                if (vision && kxv > 0)
                {
                    var (vx, _, _) = vt.Sum(st, st + (long)(ms / 1000.0 * System.Diagnostics.Stopwatch.Frequency));
                    visX = (id == learnId ? learnDx : 0) - vx / kxv;
                }
                if (!vision) need = user;
            }
            if (!vision)
            {
                // ไม่เรียนจากมือผู้ใช้แล้ว (มือไม่นิ่ง ค่าเพี้ยน) — ใช้ค่าที่คำนวณไปก่อนจนกว่าจะวัดจากภาพได้
                if (DateTime.Now - noVisionLogAt > TimeSpan.FromSeconds(30))
                {
                    noVisionLogAt = DateTime.Now;
                    Log.Info($"learn {gk}: skipped (no camera measurement for {kKey ?? "?"} yet: aim and move the mouse without firing, or scene too dark)");
                }
                return;
            }
            var cv = c.Learn.TryGetValue(gk, out var old) && old?.Length == NativeRecoil.BINS ? old : Enumerable.Repeat(1.0, NativeRecoil.BINS).ToArray();
            var parts = new List<string> { vision ? $"ภาพ k={ky:0.00}" : "มือ" };
            // แรงที่แก้ทั้งชุด: ครึ่งหนึ่งเป็นของสโคปนี้ (ค่าแยกแต่ละสโคป) อีกครึ่งเป็นของปืน (เส้นแรงดึงของปืน)
            double su = 0, sm = 0;
            for (int b = 0; b < NativeRecoil.BINS; b++) if (macroY[b] >= 15) { su += need[b]; sm += macroY[b]; }
            double mAll = sm > 0 ? Math.Clamp(su / sm, -0.35, 0.35) : 0;
            // ปกติ: ครึ่งหนึ่งของระดับแรงเป็นของสโคป; กด Shift ตลอดชุด: ระดับทั้งหมดเป็นของ "Shift ของสโคปนี้" (ปืนได้แค่รูปเส้น)
            double toScope = sk == null || mixed ? 0 : breathSpray ? mAll : 0.5 * mAll;
            string scopeSlot = breathSpray ? sk + "+shift" : sk;
            // วัดจากภาพแม่นกว่า → ปรับเร็วกว่า; จากมืออย่างเดียวมีเล็งตามเป้าปน → ปรับช้า
            int seen = c.LearnN.TryGetValue(gk, out int sn) ? sn : 0;
            double gain0 = vision ? (seen < 5 ? 0.5 : 0.3) : (seen < 5 ? 0.35 : 0.2);
            var touched = new bool[NativeRecoil.BINS];
            var rs = new double[NativeRecoil.BINS];
            int nb = 0, wild = 0;
            for (int b = 0; b < NativeRecoil.BINS; b++)
            {
                if (macroY[b] < 15) continue;
                double raw = need[b] / macroY[b] - toScope;
                rs[b] = Math.Clamp(raw, -0.35, 0.35);
                touched[b] = true;
                nb++;
                if (Math.Abs(raw) >= 0.35) wild++;
            }
            if (nb == 0) return;
            // ภาพวัดได้แปลก (ครึ่งหนึ่งของช่วงต่างเกิน 35% เช่น หันกล้องตามเป้า/ฉากเปลี่ยน) → ไม่เอาชุดนี้
            if (wild * 2 >= nb) { Log.Info($"learn {gk}: skipped (camera measurement out of range in {wild}/{nb} bins)"); return; }
            cv = (double[])cv.Clone();
            for (int b = 0; b < NativeRecoil.BINS; b++)
            {
                if (!touched[b]) continue;
                cv[b] = Math.Round(Math.Clamp(cv[b] * (1 + gain0 * rs[b]), 0.5, 1.7), 4);
                parts.Add($"{b * 0.5:0.0}s {rs[b]:+0.00;-0.00}→×{cv[b]:0.00}");
            }
            // เส้นแรงดึงต้องเรียบ: ไม่ให้ช่วงติดกันกระโดดสลับขึ้นลง (เคยเป็น ×1.49 ×0.96 ×1.28 → ช่วงหนึ่งดึงเกิน อีกช่วงขาด)
            var sm2 = (double[])cv.Clone();
            for (int b = 1; b < NativeRecoil.BINS - 1; b++)
                if (touched[b]) sm2[b] = Math.Round(0.25 * cv[b - 1] + 0.5 * cv[b] + 0.25 * cv[b + 1], 4);
            cv = sm2;
            // ช่วงหลังที่ยังไม่เคยยิงถึง เริ่มจากค่าของช่วงสุดท้ายที่เรียนแล้ว
            int lastSeen = Enumerable.Range(0, NativeRecoil.BINS).Last(b => macroY[b] >= 15 || b == 0);
            if (!c.LearnN.ContainsKey(gk)) for (int b = lastSeen + 1; b < NativeRecoil.BINS; b++) cv[b] = cv[lastSeen];
            c.Learn[gk] = cv;
            if (sk != null && Math.Abs(toScope) > 1e-4)
            {
                double os = c.LearnScope.TryGetValue(scopeSlot, out double ov) ? ov : 1;
                double ns = Math.Round(Math.Clamp(os * (1 + gain0 * toScope), 0.6, 1.6), 4);
                c.LearnScope[scopeSlot] = ns;
                parts.Add($"สโคป {scopeSlot} ×{os:0.00}→×{ns:0.00}");
            }
            else if (mixed) parts.Add("กด Shift บางช่วง: ไม่ปรับระดับสโคป");
            // ซ้ายขวา: ปืนไหลไปทางเดียวเป็นประจำ → มือดึงกลับ → จำเป็นค่า x ของปืนนั้น (หน่วยเดียวกับ x ต่อ 10 ms)
            if (!double.IsNaN(visX) && ms > 0)
            {
                // ซ้ายขวาแกว่งง่าย (ผู้เล่นหันตามเป้า) → ปรับช้า
                double xPer10 = Math.Clamp(visX / (ms / 10.0), -1.0, 1.0);
                double xb = c.LearnX.TryGetValue(gk, out double ox) ? ox : 0;
                double xn = Math.Round(Math.Clamp(xb + 0.25 * xPer10, -3, 3), 3);
                c.LearnX[gk] = xn;
                parts.Add($"x {xb:+0.00;-0.00}→{xn:+0.00;-0.00}");
            }
            c.LearnN[gk] = (c.LearnN.TryGetValue(gk, out int n) ? n : 0) + 1;
            Log.Info($"learn {gk} #{c.LearnN[gk]}: " + string.Join(" · ", parts));
            if (sk != null) ApplyScope("learn");                 // แรงของสโคปเปลี่ยน → คำนวณตัวคูณใหม่
            engine.SetCurve(HeldCurve());
            engine.SetXBias(HeldXBias());
            LiveDirty();
            learnSave ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            learnSave.Tick -= LearnSaveTick;
            learnSave.Tick += LearnSaveTick;
            learnSave.Stop();
            learnSave.Start();
        }

        void LearnSaveTick(object s, EventArgs e) { learnSave.Stop(); cfg.Save(); }

        DateTime noVisionLogAt;

        // ปืนเคาะ: หลังเตะ 0.4 วิ ดูว่ามือยังดึงลงช่วย (เตะน้อยไป) หรือดันขึ้น (เตะเกิน) → ปรับแรงต่อนัดของปืนนั้น
        const int KICK_WINDOW_MS = 400;
        int kickId = -1;
        double kickUserY, kickUserX;

        async void KickDone(double moved, int id, bool ads)
        {
            await System.Threading.Tasks.Task.Delay(KICK_WINDOW_MS + 30);
            var c = cfg.Calc;
            string gk = SlotGunKey(magSlot);
            if (c == null || !c.AutoLearn || gk == null || !RecoilCalc.SemiKick.ContainsKey(gk) || !ads || moved < 8) return;
            if (kickId != id || engine.SprayId != id) return;     // มีนัดใหม่ตามมาในช่วงวัด (เคาะรัว) → แยกไม่ได้ว่ามือแก้นัดไหน
            double uy = kickUserY, ux = kickUserX;
            if (ux > moved * 0.6) return;                       // ลากไล่เป้า ไม่นับ
            double r = Math.Clamp(uy / moved, -0.4, 0.4);
            double old = c.LearnKick.TryGetValue(gk, out double o) ? o : 1;
            double nk = Math.Round(Math.Clamp(old * (1 + 0.35 * r), 0.4, 2.5), 4);
            c.LearnKick[gk] = nk;
            c.LearnN[gk] = (c.LearnN.TryGetValue(gk, out int n) ? n : 0) + 1;
            Log.Info($"learn kick {gk} #{c.LearnN[gk]}: pulled {moved:0}, hand {uy:+0;-0} → ×{nk:0.00}");
            PushSemi();
            LiveDirty();
            learnSave ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            learnSave.Tick -= LearnSaveTick;
            learnSave.Tick += LearnSaveTick;
            learnSave.Stop();
            learnSave.Start();
        }

        // ---------------------------------------------------------------- แม็กตามปืน (ไม่อ่านหน้าจอระหว่างเล่น)
        //   ปืนที่ถือ (จากหน้ากระเป๋า) → ขนาดแม็ก, ช่องแม็กมีของ = แม็กขยาย, เวลาต่อนัดของปืนนั้น
        //   ปืนที่ไม่รู้จัก = ใช้ "กระสุน/แม็ก" ที่ตั้งเอง และ 86 ms/นัด
        ScopeDetect.Panel SlotPanelOf(int slot) => cfg.ScopeAuto && slot is 1 or 2 ? slotInfo[slot] : null;

        int MagCap(int slot)
        {
            var p = SlotPanelOf(slot);
            if (p != null && RecoilCalc.GunKey(p.Name) is string gk && RecoilCalc.GunInfo.TryGetValue(gk, out var gi))
                return p.ExtMag ? gi.ext : gi.mag;
            return cfg.MaxShots;
        }

        string SlotGunKey(int slot) => SlotPanelOf(slot) is ScopeDetect.Panel p ? RecoilCalc.GunKey(p.Name) : null;

        bool IsSemi(int slot) => SlotGunKey(slot) is string gk && RecoilCalc.SemiKick.ContainsKey(gk);

        // แรงต่อนัดของปืนเคาะ (ก่อนคูณสโคป/ของแต่ง/ท่า ซึ่งตัวยิงคูณเอง):
        //   แรงที่ตารางชุดค่านี้ดึงใน 1 นัดของปืนออโต้ (เวลาต่อนัดของ AUG) × ความแรงปืนเคาะ / ตัวคูณปืน (อยู่ในตัวคูณรวมแล้ว) × ที่เรียนรู้
        void PushSemi()
        {
            if (engine == null) return;
            string gk = SlotGunKey(magSlot);
            bool semi = gk != null && RecoilCalc.SemiKick.ContainsKey(gk);
            engine.Semi = semi;
            if (!semi || vals == null || gun < 1 || gun > vals.Length) return;
            var v = vals[gun - 1];
            var st = v.Steps ?? GHub.DefaultSteps();
            int steps = (int)Math.Round(RecoilCalc.GunInfo["AUG"].ms / 10);
            double perShot = 0;
            for (int k = 0; k < steps; k++) perShot += st[Math.Min(k, st.Length - 1)] + v.Base;
            var c = cfg.Calc ??= new CalcSettings();
            double learned = c.AutoLearn && c.LearnKick.TryGetValue(gk, out double lk) ? lk : 1;
            double kick = perShot * RecoilCalc.SemiKick[gk] / Math.Max(0.2, RecoilCalc.GunFactor(c, gk)) * learned;
            engine.SetSemiKick(kick);
        }

        double GunShotMs(int slot)
        {
            var p = SlotPanelOf(slot);
            return p != null && RecoilCalc.GunKey(p.Name) is string gk && RecoilCalc.GunInfo.TryGetValue(gk, out var gi) ? gi.ms : SHOT_MS;
        }

        // ---------------------------------------------------------------- ถืออะไรอยู่ (ตามปุ่มในเกม)
        //   ปืน 1/2 = ดึง, ปืนพก/มีด/ระเบิด = ไม่ดึง
        //   เก็บปืน (X) ในเกมเป็นปุ่มสลับ: กดอีกครั้ง = หยิบปืนเดิมกลับ
        //   ปาระเบิดเสร็จ เกมสลับกลับไปปืนเดิมเอง → ปล่อยคลิกซ้ายตอนถือระเบิด = กลับเป็นปืนเดิมหลัง ~1 วิ
        int lastGunSlot = 1;
        bool holdingThrowable;
        System.Windows.Threading.DispatcherTimer throwBack;

        void WeaponKey(int vk, int slot)
        {
            var g = GameSettings.Current;
            if (slot > 0) lastGunSlot = slot;
            if (vk == g.Holster && magSlot == 0 && !holdingThrowable) { MagSlotTo(lastGunSlot); return; }
            holdingThrowable = vk == g.Throwable;
            MagSlotTo(slot);
        }

        void ThrowReleased()
        {
            if (!holdingThrowable || magSlot != 0 || !Playing()) return;
            if (throwBack == null)
            {
                throwBack = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
                throwBack.Tick += (s, e) =>
                {
                    throwBack.Stop();
                    if (!holdingThrowable || magSlot != 0) return;
                    holdingThrowable = false;
                    Log.Info($"threw grenade → back to gun slot {lastGunSlot}");
                    MagSlotTo(lastGunSlot);
                };
            }
            throwBack.Stop();
            throwBack.Start();
        }

        // ปุ่มอาวุธตามที่ตั้งในเกม: 1 / 2 = ช่องปืน, 0 = ปืนพก / มีด / ระเบิด / เก็บปืน, -1 = ไม่ใช่ปุ่มอาวุธ
        static int WeaponSlotOf(int vk)
        {
            var g = GameSettings.Current;
            if (vk == g.Slot1) return 1;
            if (vk == g.Slot2) return 2;
            if (vk == g.Pistol || vk == g.Melee || vk == g.Throwable || vk == g.Holster) return 0;
            return -1;
        }

        void OnGameSettingsChanged()
        {
            pubgSlotKeys = null;
            stanceKeys = null;
            var g = GameSettings.Current;
            Log.Info($"pubg settings: ads {g.Ads} mag {g.AdsMagnified} universal {g.Universal} vertical {g.Vertical} adsMode {g.AdsMode} " +
                     $"keys {KeyName(g.Slot1)}/{KeyName(g.Slot2)}/{KeyName(g.Pistol)}/{KeyName(g.Melee)}/{KeyName(g.Throwable)}");
            BuildKeyCards();
            BuildGameCard();
            if (cfg.Calc != null) CalcChanged();
            LiveDirty();
        }

        void GameReload_Click(object sender, RoutedEventArgs e)
        {
            GameSettings.Reload();
            OnGameSettingsChanged();
            Flash(GameSettings.Current.Found ? "อ่านการตั้งค่า PUBG ใหม่แล้ว" : "ไม่พบไฟล์ตั้งค่า PUBG");
        }

        void GameRef_Click(object sender, RoutedEventArgs e)
        {
            if (!Ask("ใช้ความไวในเกมตอนนี้เป็น \"ค่าตอนจูนตาราง\"?\n\nกดใช่ ถ้าเพิ่งจูนตารางแรงดีดใหม่ด้วยความไวตอนนี้\n(ไม่งั้นไม่ต้องกด — โปรแกรมชดเชยเองเมื่อเปลี่ยนความไว)")) return;
            var c = cfg.Calc ??= new CalcSettings();
            RecoilCalc.SnapshotRef(c);
            cfg.Save();
            CalcChanged();
            BuildGameCard();
            Flash("จำความไวตอนนี้เป็นค่าตอนจูนแล้ว");
        }

        void BuildGameCard()
        {
            if (GameRows == null) return;
            var g = GameSettings.Current;
            var c = cfg.Calc ??= new CalcSettings();
            GameRows.Children.Clear();
            if (!g.Found)
            {
                GameRows.Children.Add(T("ไม่พบไฟล์ตั้งค่า PUBG — เปิดเกมหนึ่งครั้งแล้วกดอ่านใหม่", 12, PAmber));
                return;
            }
            string S(double v) => v.ToString("0.##");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel();
            var right = new StackPanel();
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(right);
            void Add(StackPanel col, string label, string note, string value, Brush vb = null)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0), Height = 30 };
                var v = new Border
                {
                    CornerRadius = new CornerRadius(7), Background = B("#121B30"), BorderBrush = B("#24345A"), BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 1, 8, 2), VerticalAlignment = VerticalAlignment.Center, Child = T(value, 11.5, vb ?? PText, FontWeights.Bold, Mono)
                };
                DockPanel.SetDock(v, Dock.Right);
                row.Children.Add(v);
                var l = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                l.Children.Add(T(label, 12, PText, FontWeights.SemiBold));
                if (note != null) { var n = T("  " + note, 10, PFaint); l.Children.Add(n); }
                row.Children.Add(l);
                col.Children.Add(row);
                col.Children.Add(new Border { Height = 1, Background = PLine });
            }
            Add(left, "ความไวทั่วไป", null, S(g.General));
            Add(left, "ความไวตอนเล็ง (ADS)", "Red/Holo/ศูนย์เหล็ก", S(g.Ads));
            Add(left, "ตัวคูณความไวแนวตั้ง", "มีผลกับแรงดึง", g.Vertical.ToString("0.00"));
            Add(left, "ความไวเดียวทุกสโคป", g.Universal ? "ทุกสโคปขยายใช้ค่าเดียว" : "แยกตามสโคป", g.Universal ? "เปิด" : "ปิด", g.Universal ? PGreen : PAmber);
            Add(left, "โหมดเล็ง", null, g.AdsMode == "Toggle" ? "กดสลับ" : "กดค้าง");
            foreach (var (lab, key) in new[] { ("2x", "Scope2X"), ("3x", "Scope3X"), ("4x", "Scope4X"), ("6x", "Scope6X"), ("8x", "Scope8X"), ("15x", "Scope15X") })
                Add(right, $"สโคป {lab}", g.Universal ? "(ใช้ค่าเดียว)" : null, S(g.SensFor(key)));

            var comp = string.Join("  ·  ", cfg.Names.Select(n => (n, sc: RecoilCalc.SetScope(n))).Where(t => t.sc != null)
                .Select(t => $"{t.n} ×{RecoilCalc.SensFactor(c, t.sc):0.00}"));
            var keys = $"ปืน 1 {KeyName(g.Slot1)} · ปืน 2 {KeyName(g.Slot2)} · ปืนพก {KeyName(g.Pistol)} · มีด {KeyName(g.Melee)} · ระเบิด {KeyName(g.Throwable)} · " +
                       $"เก็บปืน {KeyName(g.Holster)} · ย่อ {KeyName(g.Crouch)} · หมอบ {KeyName(g.Prone)}";
            GameRows.Children.Add(grid);
            var foot = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            foot.Children.Add(T("ชดเชยความไวเทียบตอนจูนตาราง:  " + (comp.Length > 0 ? comp : "—") + (c.RefNeed.Count == 0 ? "  (ยังไม่ได้จำค่าตอนจูน)" : ""), 11.5, PCyan, FontWeights.SemiBold));
            var k = T("ปุ่มในเกม:  " + keys, 11, PMuted);
            k.TextWrapping = TextWrapping.Wrap;
            k.Margin = new Thickness(0, 4, 0, 0);
            foot.Children.Add(k);
            GameRows.Children.Add(foot);
        }
    }

    static class UiExt
    {
        public static T Also<T>(this T t, Action<T> a) { a(t); return t; }
    }
}
