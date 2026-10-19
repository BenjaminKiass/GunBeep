using System.Threading;
using System.Windows;

namespace GunBeep
{
    public partial class App : Application
    {
        static Mutex mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            mutex = new Mutex(true, "GunBeep_single_instance", out bool created);
            if (!created) { Shutdown(); return; }   // เปิดอยู่แล้ว
            new MainWindow().Show();
        }
    }
}
