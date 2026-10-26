using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lumos
{
    /// <summary>
    /// Спливаюче вікно над значком у треї. Створюється при відкритті і повністю закривається,
    /// коли клікнеш деінде, щоб не тримати в пам'яті інтерфейс, якого не видно.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        // Прозорі відступи навколо карточки (місце для тіні), див. Grid Margin у XAML.
        private const double PadLeft = 16, PadTop = 12, PadRight = 16, PadBottom = 20;

        private readonly OverlayViewModel _vm;
        private System.Drawing.Point _anchorPx;
        private bool _closing;

        public OverlayWindow(OverlayViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            SizeChanged += (_, _) => Place();
            Deactivated += (_, _) => RequestClose();
            vm.CloseRequested += RequestClose;
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

        /// <summary>Закрити вікно. Через диспетчер, бо його часто викликають з обробника Deactivated.</summary>
        public void RequestClose()
        {
            if (_closing) return;
            _closing = true;
            Dispatcher.BeginInvoke(new Action(Close));
        }

        protected override void OnClosed(EventArgs e)
        {
            _vm.CloseRequested -= RequestClose;
            DataContext = null;
            base.OnClosed(e);
        }

        private void Place()
        {
            if (!IsVisible) return;

            // Рахуємо у фізичних пікселях монітора, на якому значок, з його власним масштабом.
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
            System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromPoint(_anchorPx);
            System.Drawing.Rectangle area = screen.WorkingArea;
            System.Drawing.Rectangle bounds = screen.Bounds;

            double w = ActualWidth * sx, h = ActualHeight * sy;
            double padL = PadLeft * sx, padT = PadTop * sy, padR = PadRight * sx, padB = PadBottom * sy;
            double gap = 8 * sx;

            double left, top;
            if (area.Top > bounds.Top)
            {
                // Панель завдань угорі: відкриваємося під нею.
                top = area.Top + gap - padT;
                left = _anchorPx.X - w / 2;
            }
            else if (area.Left > bounds.Left)
            {
                // Панель зліва.
                left = area.Left + gap - padL;
                top = _anchorPx.Y - h / 2;
            }
            else if (area.Right < bounds.Right)
            {
                // Панель справа.
                left = area.Right - gap - w + padR;
                top = _anchorPx.Y - h / 2;
            }
            else
            {
                // Звичайний випадок: панель знизу, відкриваємося над значком.
                top = area.Bottom - gap - h + padB;
                left = _anchorPx.X - w / 2;
            }

            // Не виходити за межі робочої області.
            left = Math.Max(area.Left - padL + gap, Math.Min(left, area.Right - w + padR - gap));
            top = Math.Max(area.Top - padT + gap, Math.Min(top, area.Bottom - h + padB - gap));

            Left = Math.Round(left) / sx;
            Top = Math.Round(top) / sy;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Dispatcher.BeginInvoke(new Action(Place));
        }
    }

    /// <summary>Повертає системі пам'ять, яку програма вже не використовує.</summary>
    internal static class MemoryHelper
    {
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        public static void Trim()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch (Exception ex)
            {
                Log.Write("Trim: " + ex.Message);
            }
        }
    }
}
