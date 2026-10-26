using System;
using Velopack;

namespace Lumos
{
    public static class Program
    {
        /// <summary>Версія, до якої щойно оновилися (щоб показати сповіщення після перезапуску).</summary>
        public static string? UpdatedTo { get; private set; }

        /// <summary>Перший запуск після встановлення: показати вікно «Lumos встановлено».</summary>
        public static bool FirstRun { get; private set; }

        [STAThread]
        public static void Main(string[] args)
        {
            // Має бути першим: інсталятор і оновлювач запускають програму з особливими аргументами.
            VelopackApp.Build()
                .OnBeforeUninstallFastCallback(_ => AutostartHelper.Apply(false))
                .OnFirstRun(_ => FirstRun = true)
                .OnRestarted(v => UpdatedTo = v?.ToString())
                .Run();

            // Режим спільного збирача (запускається на GitHub за розкладом): без вікон і трею.
            if (args.Length == 2 && args[0] == "--collect")
            {
                Environment.Exit(CentralData.CollectAsync(args[1]).GetAwaiter().GetResult());
                return;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
