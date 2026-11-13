using System;
using System.Threading;
using System.Windows;

namespace GunBeep
{
    public partial class App : Application
    {
        static Mutex mutex;
        static EventWaitHandle showSignal, quitSignal;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            mutex = new Mutex(true, "GunBeep_single_instance", out bool created);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "GunBeep_show");
            // "GunBeep.exe --quit" = ปิดตัวที่รันอยู่แบบสะอาด (ลบไอคอนถาด, คืนความละเอียดจอ, เขียนไฟล์ค้างให้เสร็จ)
            //   ใช้ตอนติดตั้ง/อัปเดต แทนการฆ่าโปรเซส (ฆ่าแล้วไอคอนค้างในถาด ชี้แล้วหาย ปักหมุดไม่ได้)
            quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "GunBeep_quit");
            bool quit = Array.IndexOf(e.Args, "--quit") >= 0;
            if (!created)
            {
                // เปิดอยู่แล้ว (เช่นอยู่ที่ถาด) → บอกตัวเดิมให้โชว์หน้าต่าง (หรือปิดตัวเองถ้า --quit)
                if (quit) quitSignal.Set(); else showSignal.Set();
                Shutdown();
                return;
            }
            if (quit) { Shutdown(); return; }                   // ไม่มีตัวไหนรันอยู่ ไม่ต้องทำอะไร
            // พังเมื่อไหร่ เขียนสาเหตุลง saved\log.txt — error ในหน้าต่างไม่ทำให้ทั้งโปรแกรมปิด
            Log.Info($"start v{Updater.Current} args=[{string.Join(" ", e.Args)}]");
            DispatcherUnhandledException += (s, a) => { Log.Error("UI", a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log.Error("crash", a.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, a) => { Log.Error("task", a.Exception); a.SetObserved(); };

            ShutdownMode = ShutdownMode.OnMainWindowClose;   // ป้ายบนจอเกมเป็นหน้าต่างที่สอง
            var w = new MainWindow();
            MainWindow = w;
            ThreadPool.RegisterWaitForSingleObject(showSignal,
                (s, t) => w.Dispatcher.BeginInvoke(new Action(w.ShowFromTray)), null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(quitSignal,
                (s, t) => w.Dispatcher.BeginInvoke(new Action(() => { Log.Info("quit requested (--quit)"); w.Close(); })), null, -1, true);
            if (Array.IndexOf(e.Args, "--tray") >= 0) w.StartInTray();
            else w.Show();
        }
    }
}
