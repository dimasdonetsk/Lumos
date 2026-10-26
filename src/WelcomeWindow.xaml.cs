using System.Windows;
using System.Windows.Input;

namespace Lumos
{
    /// <summary>Вікно «Lumos встановлено», показується при першому запуску після встановлення.</summary>
    public partial class WelcomeWindow : Window
    {
        public WelcomeWindow()
        {
            InitializeComponent();
            VersionText.Text = "Версія " + Updater.CurrentVersion;
        }

        private void OnDone(object sender, RoutedEventArgs e) => Close();

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }
    }
}
