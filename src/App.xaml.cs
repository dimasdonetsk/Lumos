using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Lumos
{
    public partial class App : Application
    {
        private Mutex? _mutex;
        private AppController? _controller;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Лише одна копія програми.
            _mutex = new Mutex(true, "Lumos.SingleInstance.v1", out bool createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnUnhandled;

            _controller = new AppController();
            _controller.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _controller?.Dispose();
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("Unhandled: " + e.Exception);
            e.Handled = true;
        }
    }

    internal static class Log
    {
        public static void Write(string text)
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                string path = Path.Combine(Settings.DataDir, "log.txt");
                // Журнал не росте безкінечно: понад 256 КБ починаємо заново.
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 256 * 1024) info.Delete();
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
            }
            catch { }
        }
    }
}
