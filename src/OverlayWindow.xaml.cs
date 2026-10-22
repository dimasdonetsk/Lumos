using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lumos
{
    public partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <summary>TopCenter, TopLeft або TopRight.</summary>
        public string Position { get; set; } = "TopCenter";

        public OverlayWindow(OverlayViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            SizeChanged += (_, _) => Reposition();
            vm.LayoutChanged += () => Dispatcher.BeginInvoke(new Action(Reposition));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Не показуватися в Alt+Tab і не забирати фокус у вікна, з яким працюєш.
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        public void Reposition()
        {
            Rect area = SystemParameters.WorkArea;
            double width = ActualWidth > 0 ? ActualWidth : Width;
            Left = Position switch
            {
                "TopLeft" => area.Left - 4,
                "TopRight" => area.Right - width + 4,
                _ => area.Left + (area.Width - width) / 2
            };
            Top = area.Top - 4;
        }
    }
}
