using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;

namespace Zink
{
    public sealed partial class AreaSelectionWindow : Window
    {
        private readonly TaskCompletionSource<Rectangle?> _completion = new();
        private global::Windows.Foundation.Point _start;
        private Point _desktopStart;
        private bool _selecting;
        private readonly int _virtualLeft;
        private readonly int _virtualTop;
        private bool _completed;
        private readonly string _previewPath;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        private AreaSelectionWindow()
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);

            _virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            _virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            _previewPath = Path.Combine(
                ApplicationData.Current.TemporaryFolder.Path,
                $"ZinkAreaSelection-{Guid.NewGuid():N}.png");
            CreateDesktopPreview(width, height);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
            appWindow.MoveAndResize(new RectInt32(_virtualLeft, _virtualTop, width, height));

            Activated += (_, _) => SelectionSurface.Focus(FocusState.Programmatic);
            SelectionSurface.SizeChanged += (_, _) => UpdateShadeRegions(0, 0, 0, 0);
            Closed += (_, _) =>
            {
                Complete(null);
                try { File.Delete(_previewPath); } catch { }
            };
        }

        public static async Task<Rectangle?> SelectRegionAsync()
        {
            var window = new AreaSelectionWindow();
            window.Activate();
            return await window._completion.Task;
        }

        private void SelectionSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _start = e.GetCurrentPoint(SelectionSurface).Position;
            GetCursorPos(out _desktopStart);
            _selecting = true;
            SelectionSurface.CapturePointer(e.Pointer);
            SelectionRectangle.Visibility = Visibility.Visible;
            UpdateSelection(_start);
        }

        private void SelectionSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_selecting)
                UpdateSelection(e.GetCurrentPoint(SelectionSurface).Position);
        }

        private void SelectionSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_selecting)
                return;

            var end = e.GetCurrentPoint(SelectionSurface).Position;
            GetCursorPos(out var desktopEnd);
            _selecting = false;
            SelectionSurface.ReleasePointerCapture(e.Pointer);

            var x = Math.Min(_desktopStart.X, desktopEnd.X);
            var y = Math.Min(_desktopStart.Y, desktopEnd.Y);
            var width = Math.Abs(desktopEnd.X - _desktopStart.X);
            var height = Math.Abs(desktopEnd.Y - _desktopStart.Y);

            Complete(width >= 8 && height >= 8 ? new Rectangle(x, y, width, height) : null);
            Close();
        }

        private void UpdateSelection(global::Windows.Foundation.Point point)
        {
            var x = Math.Min(_start.X, point.X);
            var y = Math.Min(_start.Y, point.Y);
            var width = Math.Abs(point.X - _start.X);
            var height = Math.Abs(point.Y - _start.Y);
            Canvas.SetLeft(SelectionRectangle, x);
            Canvas.SetTop(SelectionRectangle, y);
            SelectionRectangle.Width = width;
            SelectionRectangle.Height = height;
            UpdateShadeRegions(x, y, width, height);
            SizeText.Text = $"{(int)width} × {(int)height}";
        }

        private void CreateDesktopPreview(int width, int height)
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    new System.Drawing.Point(_virtualLeft, _virtualTop),
                    System.Drawing.Point.Empty,
                    new System.Drawing.Size(width, height),
                    CopyPixelOperation.SourceCopy);
            }
            bitmap.Save(_previewPath, ImageFormat.Png);
            DesktopPreview.Source = new BitmapImage(
                new Uri($"ms-appdata:///temp/{Path.GetFileName(_previewPath)}"));
        }

        private void UpdateShadeRegions(double x, double y, double width, double height)
        {
            var surfaceWidth = SelectionSurface.ActualWidth;
            var surfaceHeight = SelectionSurface.ActualHeight;
            if (surfaceWidth <= 0 || surfaceHeight <= 0)
                return;

            TopShade.Width = surfaceWidth;
            TopShade.Height = y;
            Canvas.SetLeft(TopShade, 0);
            Canvas.SetTop(TopShade, 0);

            LeftShade.Width = x;
            LeftShade.Height = height;
            Canvas.SetLeft(LeftShade, 0);
            Canvas.SetTop(LeftShade, y);

            RightShade.Width = Math.Max(0, surfaceWidth - x - width);
            RightShade.Height = height;
            Canvas.SetLeft(RightShade, x + width);
            Canvas.SetTop(RightShade, y);

            BottomShade.Width = surfaceWidth;
            BottomShade.Height = Math.Max(0, surfaceHeight - y - height);
            Canvas.SetLeft(BottomShade, 0);
            Canvas.SetTop(BottomShade, y + height);
        }

        private void SelectionSurface_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                Complete(null);
                Close();
            }
        }

        private void Complete(Rectangle? result)
        {
            if (_completed)
                return;
            _completed = true;
            _completion.TrySetResult(result);
        }
    }
}
