using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lumos
{
    /// <summary>Спливаюче вікно над значком у треї. Ховається, щойно клікнеш деінде.</summary>
    public partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        // Прозорі відступи навколо карточки (місце для тіні), див. Grid Margin у XAML.
        private const double PadLeft = 16, PadTop = 12, PadRight = 16, PadBottom = 20;

        private System.Drawing.Point _anchorPx;

        /// <summary>Коли вікно сховалося востаннє (щоб клік по значку не відкривав його одразу знову).</summary>
        public DateTime LastHidden { get; private set; } = DateTime.MinValue;

        public OverlayWindow(OverlayViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            SizeChanged += (_, _) => Place();
            Deactivated += (_, _) => HideFlyout();
            vm.CloseRequested += HideFlyout;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Не показуватися в Alt+Tab і на панелі завдань.
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        }

        /// <summary>Показати над точкою на екрані (у фізичних пікселях), зазвичай над значком у треї.</summary>
        public void ShowAt(System.Drawing.Point anchorPx)
        {
            _anchorPx = anchorPx;
            if (!IsVisible) Show();
            Place();
            Activate();
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
        }

        public void HideFlyout()
        {
            if (!IsVisible) return;
            Hide();
            LastHidden = DateTime.Now;
        }

        private void Place()
        {
            if (!IsVisible) return;
            PresentationSource? source = PresentationSource.FromVisual(this);
            Matrix toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            Point anchor = toDip.Transform(new Point(_anchorPx.X, _anchorPx.Y));

            Rect area = SystemParameters.WorkArea;
            double screenW = SystemParameters.PrimaryScreenWidth;
            double w = ActualWidth, h = ActualHeight;
            const double gap = 8;

            double left, top;
            if (area.Top > 0)
            {
                // Панель завдань угорі: відкриваємося під нею.
                top = area.Top + gap - PadTop;
                left = anchor.X - w / 2;
            }
            else if (area.Left > 0)
            {
                // Панель зліва.
                left = area.Left + gap - PadLeft;
                top = anchor.Y - h / 2;
            }
            else if (area.Right < screenW)
            {
                // Панель справа.
                left = area.Right - gap - w + PadRight;
                top = anchor.Y - h / 2;
            }
            else
            {
                // Звичайний випадок: панель знизу, відкриваємося над значком.
                top = area.Bottom - gap - h + PadBottom;
                left = anchor.X - w / 2;
            }

            // Не виходити за межі робочої області.
            left = Math.Max(area.Left - PadLeft + gap, Math.Min(left, area.Right - w + PadRight - gap));
            top = Math.Max(area.Top - PadTop + gap, Math.Min(top, area.Bottom - h + PadBottom - gap));

            Left = left;
            Top = top;
        }
    }
}
