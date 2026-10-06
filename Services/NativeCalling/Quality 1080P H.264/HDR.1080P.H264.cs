using System;
using Microsoft.Graphics.Display;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Zink.Services.NativeCalling.Quality1080p
{
    /// <summary>
    /// Detects whether Windows HDR is active for the display containing Zink's
    /// 1080p screen-share window. Detection fails closed for SDR safety.
    /// </summary>
    internal static class HDR
    {
        internal readonly record struct DisplayState(bool IsSupported, bool IsEnabled, string Kind, string? Error);

        public static DisplayState GetDisplayState()
        {
            try
            {
                Window? window = App.MainWindow;
                if (window == null)
                    return new(false, false, "Unavailable", "The main window is unavailable.");

                IntPtr hwnd = WindowNative.GetWindowHandle(window);
                if (hwnd == IntPtr.Zero)
                    return new(false, false, "Unavailable", "The main window handle is unavailable.");

                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                using var display = DisplayInformation.CreateForWindowId(windowId);
                var colour = display.GetAdvancedColorInfo();
                bool supported = colour.IsAdvancedColorKindAvailable(DisplayAdvancedColorKind.HighDynamicRange);
                bool enabled = colour.CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange;
                return new(supported, enabled, colour.CurrentAdvancedColorKind.ToString(), null);
            }
            catch (Exception ex)
            {
                return new(false, false, "Unavailable", ex.Message);
            }
        }
    }
}
