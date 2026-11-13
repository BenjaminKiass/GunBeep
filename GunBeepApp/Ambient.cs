using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GunBeep
{
    /// <summary>
    /// แอนิเมชันวนตลอดของหน้าต่างหลัก (ขอบหมุน, แสงออโรร่าพื้นหลัง, โลโก้/ปุ่ม ON เรืองแสง, วงแหวนรอตรวจจับ, จุด LIVE).
    /// ทุกตัวเป็น AnimationClock ที่ "หยุดชั่วคราว" ได้ (Pause/Resume) → หยุดแล้วเล่นต่อจากตำแหน่งเดิม ไม่กระตุก.
    /// ไม่ใช้ BlurEffect / DropShadowEffect กับของที่ขยับ (แสงเป็น RadialGradientBrush) และจำกัด 30 เฟรม/วินาที.
    /// MainWindow สั่ง Set(run, on): run = หน้าต่างมองเห็นได้ (ไม่ซ่อน/ไม่ย่อ และ PUBG ไม่ได้อยู่หน้าสุด).
    /// </summary>
    sealed class Ambient
    {
        public const int FPS = 30;

        readonly FrameworkElement win;
        readonly List<AnimationClock> loop = new();     // วนตลอด ไม่ขึ้นกับสถานะ ON/OFF
        readonly List<(IAnimatable obj, DependencyProperty dp, AnimationClock clock)> state = new();   // ขึ้นกับ ON/OFF
        bool built, running;
        bool? stateOn;

        public Ambient(FrameworkElement window) { win = window; }

        T Find<T>(string name) where T : class => win.FindName(name) as T;

        static T Rate<T>(T t) where T : Timeline { Timeline.SetDesiredFrameRate(t, FPS); return t; }

        static readonly IEasingFunction Sine = new SineEase { EasingMode = EasingMode.EaseInOut };

        AnimationClock Apply(IAnimatable obj, DependencyProperty dp, AnimationTimeline a)
        {
            var c = Rate(a).CreateClock();
            obj.ApplyAnimationClock(dp, c);
            return c;
        }

        static DoubleAnimation Wave(double from, double to, double sec, double beginSec = 0) =>
            new DoubleAnimation(from, to, TimeSpan.FromSeconds(sec))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Sine,
                BeginTime = TimeSpan.FromSeconds(-beginSec)       // เริ่มกลางคาบ ไม่ให้ทุกอันเริ่มพร้อมกัน
            };

        void Build()
        {
            built = true;
            if (Find<RotateTransform>("EdgeSpin") is RotateTransform edge)
                loop.Add(Apply(edge, RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever }));

            // ออโรร่า: 3 ดวงลอยช้าๆ คาบไม่ลงตัวกัน (ไม่เห็นว่าวนซ้ำ)
            double[,] p = { { 170, 120, 23, 31 }, { -190, 140, 29, 19 }, { 150, -130, 37, 27 } };
            for (int i = 0; i < 3; i++)
            {
                if (Find<TranslateTransform>("AuroraT" + (i + 1)) is not TranslateTransform t) continue;
                loop.Add(Apply(t, TranslateTransform.XProperty, Wave(-p[i, 0], p[i, 0], p[i, 2], p[i, 2] * 0.37 * (i + 1))));
                loop.Add(Apply(t, TranslateTransform.YProperty, Wave(-p[i, 1], p[i, 1], p[i, 3], p[i, 3] * 0.61 * (i + 1))));
            }
            if (Find<UIElement>("LogoGlow") is UIElement logo)
                loop.Add(Apply(logo, UIElement.OpacityProperty, Wave(0.35, 0.9, 1.8)));
            if (Find<RotateTransform>("HeroSpin") is RotateTransform hero)
                loop.Add(Apply(hero, RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever }));
            if (Find<UIElement>("HeroHalo") is UIElement halo)
                loop.Add(Apply(halo, UIElement.OpacityProperty, Wave(0.35, 0.85, 2.2)));
            // สร้างตอนหน้าต่างยังซ่อนอยู่ (เช่นเปิดแบบ --tray) → หยุดไว้ก่อน ไม่งั้นวิ่งทั้งที่มองไม่เห็น
            if (!running) foreach (var c in loop) c.Controller?.Pause();
        }

        void ClearState()
        {
            foreach (var (obj, dp, clock) in state)
            {
                clock.Controller?.Stop();
                obj.ApplyAnimationClock(dp, null);
            }
            state.Clear();
        }

        void AddState(IAnimatable obj, DependencyProperty dp, AnimationTimeline a) => state.Add((obj, dp, Apply(obj, dp, a)));

        void BuildState(bool on)
        {
            ClearState();
            stateOn = on;
            if (Find<UIElement>("Glow") is UIElement glow)
            {
                if (on)
                {
                    AddState(glow, UIElement.OpacityProperty, Wave(0.55, 1.0, 1.4));
                    if (glow.RenderTransform is ScaleTransform gs && !gs.IsFrozen)
                    {
                        AddState(gs, ScaleTransform.ScaleXProperty, Wave(0.93, 1.05, 1.4));
                        AddState(gs, ScaleTransform.ScaleYProperty, Wave(0.93, 1.05, 1.4));
                    }
                }
                else
                {
                    AddState(glow, UIElement.OpacityProperty, Wave(0.18, 0.32, 3.2));
                    if (glow.RenderTransform is ScaleTransform gs && !gs.IsFrozen)
                    {
                        AddState(gs, ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(300)));
                        AddState(gs, ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(300)));
                    }
                }
            }
            if (Find<UIElement>("LiveDot") is UIElement dot)
                AddState(dot, UIElement.OpacityProperty, on ? Wave(0.3, 1.0, 0.9) : new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            if (!running) foreach (var s in state) s.clock.Controller?.Pause();
        }

        /// <summary>run = ควรขยับ (มองเห็นหน้าต่าง), on = มาโครเปิด. เรียกซ้ำบ่อยได้ — ทำงานเฉพาะเมื่อค่าเปลี่ยน.</summary>
        public void Set(bool run, bool on)
        {
            if (!built) Build();
            if (stateOn != on) BuildState(on);
            if (run == running) return;
            running = run;
            foreach (var c in loop) { if (run) c.Controller?.Resume(); else c.Controller?.Pause(); }
            foreach (var s in state) { if (run) s.clock.Controller?.Resume(); else s.clock.Controller?.Pause(); }
        }

        public bool Running => running;

        /// <summary>แสงวงกลมสีเดียว (เข้มตรงกลาง จางออกขอบ) แทน BlurEffect.</summary>
        public static RadialGradientBrush GlowBrush(Color c) => new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0xE6, c.R, c.G, c.B), 0.38),
            new GradientStop(Color.FromArgb(0x55, c.R, c.G, c.B), 0.62),
            new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
        });

        /// <summary>เปลี่ยนสีแสง (คงความโปร่งของแต่ละจุดไว้).</summary>
        public static void GlowTo(RadialGradientBrush b, Color c, TimeSpan d)
        {
            foreach (var s in b.GradientStops)
                s.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(s.Color.A, c.R, c.G, c.B), d));
        }

        /// <summary>เปลี่ยนหน้าแบบลื่น: จางเข้า + เลื่อนขึ้นเล็กน้อย (200 ms).</summary>
        public static void PageIn(FrameworkElement page)
        {
            if (page == null) return;
            var tt = page.RenderTransform as TranslateTransform;
            if (tt == null || tt.IsFrozen) page.RenderTransform = tt = new TranslateTransform();
            var d = TimeSpan.FromMilliseconds(220);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, d) { EasingFunction = ease });
        }
    }
}
