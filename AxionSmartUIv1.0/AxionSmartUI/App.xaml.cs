using System;
using System.Threading;
using System.Windows;

namespace AxionSmartUI
{
    public partial class App : System.Windows.Application
    {
        //  GUID
        private static Mutex _mutex = new Mutex(true, "{51d7c34b-4c28-4e89-8b01-83c9a6f3b0b5}");

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!_mutex.WaitOne(TimeSpan.Zero, true))
            {
                
                System.Windows.MessageBox.Show("Axion Smart UI ya se está ejecutando.", "Axion", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
           
            _mutex.ReleaseMutex();
            base.OnExit(e);
        }
    }
}