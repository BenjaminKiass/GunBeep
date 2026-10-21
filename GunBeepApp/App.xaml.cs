using System;
using System.Threading;
using System.Windows;

namespace GunBeep
{
    public partial class App : Application
    {
        static Mutex mutex;
        static EventWaitHandle showSignal;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            mutex = new Mutex(true, "GunBeep_single_instance", out bool created);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "GunBeep_show");
            if (!created)
            {
                // เปิดอยู่แล้ว (เช่นอยู่ที่ถาด) → บอกตัวเดิมให้โชว์หน้าต่าง
                showSignal.Set();
                Shutdown();
                return;
            }
            var w = new MainWindow();
            ThreadPool.RegisterWaitForSingleObject(showSignal,
                (s, t) => w.Dispatcher.BeginInvoke(new Action(w.ShowFromTray)), null, -1, false);
            if (Array.IndexOf(e.Args, "--tray") >= 0) w.StartInTray();
            else w.Show();
        }
    }
}
