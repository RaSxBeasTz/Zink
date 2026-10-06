using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Zink.Interop;

namespace Zink.Services.Recording
{
    public sealed class HotkeyService : IDisposable
    {
        private const int SaveLast45HotkeyId = 5001;
        private const uint VkZ = 0x5A;
        private const int VkControl = 0x11;

        private readonly IntPtr _hwnd;
        private IntPtr _oldWndProc;
        private IntPtr _keyboardHook;
        private WndProcDelegate? _newWndProc;
        private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
        private bool _zIsDown;
        private long _lastTriggerTicks;

        public HotkeyService(IntPtr hwnd)
        {
            _hwnd = hwnd;
        }

        public void Initialize()
        {
            _newWndProc = CustomWndProc;
            _oldWndProc = NativeMethods.SetWindowLongPtr(
                _hwnd,
                NativeMethods.GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_newWndProc));

            bool registered = NativeMethods.RegisterHotKey(
                _hwnd,
                SaveLast45HotkeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT,
                VkZ);

            _keyboardProc = LowLevelKeyboardCallback;
            _keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _keyboardProc,
                NativeMethods.GetModuleHandle(null),
                0);

            _ = GameReplayLog.WriteAsync(nameof(HotkeyService),
                $"Ctrl+Z global replay shortcut initialized. RegisterHotKey={registered}, LowLevelHook={_keyboardHook != IntPtr.Zero}");
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == SaveLast45HotkeyId)
            {
                TriggerReplaySave("RegisterHotKey");
            }

            return NativeMethods.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                uint virtualKey = unchecked((uint)Marshal.ReadInt32(lParam));
                if (virtualKey == VkZ)
                {
                    if (message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP)
                    {
                        _zIsDown = false;
                    }
                    else if ((message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN) &&
                             !_zIsDown)
                    {
                        _zIsDown = true;
                        if ((NativeMethods.GetAsyncKeyState(VkControl) & 0x8000) != 0)
                            TriggerReplaySave("LowLevelKeyboardHook");
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private void TriggerReplaySave(string source)
        {
            long now = Environment.TickCount64;
            long previous = System.Threading.Interlocked.Read(ref _lastTriggerTicks);
            if (now - previous < 750)
            {
                _ = GameReplayLog.WriteAsync(nameof(HotkeyService),
                    $"Ctrl+Z ignored as duplicate. Source={source}, SincePreviousMs={now - previous}");
                return;
            }
            System.Threading.Interlocked.Exchange(ref _lastTriggerTicks, now);
            _ = GameReplayLog.WriteAsync(nameof(HotkeyService),
                $"Ctrl+Z accepted. Source={source}, Memory={GetMemorySnapshot()}");
            _ = SaveReplayFromHotkeyAsync(source);
        }

        private static async Task SaveReplayFromHotkeyAsync(string source)
        {
            try
            {
                if (!RecordingPreferences.IsHotkeyGameClipRecordingEnabled)
                {
                    await GameReplayLog.WriteAsync(nameof(HotkeyService),
                        $"Ctrl+Z save skipped because hotkey recording is disabled. Source={source}");
                    return;
                }

                int replaySeconds = RecordingPreferences.BackgroundReplayDurationSeconds;
                ShowPopup("Zink Replay", $"Recording the last {FormatDuration(replaySeconds)}.");

                string? savedPath = await RecordingManager.Instance.SaveLastReplayAsync(
                    TimeSpan.FromSeconds(replaySeconds));
                if (string.IsNullOrWhiteSpace(savedPath))
                {
                    await GameReplayLog.WriteAsync(nameof(HotkeyService),
                        $"Ctrl+Z save returned no path. Source={source}, Memory={GetMemorySnapshot()}");
                    return;
                }

                ShowPopup("Zink Replay", $"Saved: {Path.GetFileName(savedPath)}");
                await GameReplayLog.WriteAsync(nameof(HotkeyService),
                    $"Ctrl+Z save completed. Source={source}, Output={savedPath}, Memory={GetMemorySnapshot()}");
            }
            catch (Exception ex)
            {
                await GameReplayLog.WriteAsync(nameof(HotkeyService),
                    $"Ctrl+Z save failed. Source={source}, Memory={GetMemorySnapshot()}, Error={ex}");
                ShowPopup("Zink Replay Error", ex.Message);
            }
        }

        private static string GetMemorySnapshot()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return $"WorkingSetMB={process.WorkingSet64 / 1048576d:0.0}, PrivateMB={process.PrivateMemorySize64 / 1048576d:0.0}, ManagedHeapMB={GC.GetTotalMemory(false) / 1048576d:0.0}";
        }

        private static string FormatDuration(int seconds) => seconds >= 60
            ? $"{seconds / 60} minute{(seconds == 60 ? string.Empty : "s")}" 
            : $"{seconds} seconds";

        private static void ShowPopup(string title, string message)
        {
            try
            {
                var notification = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message)
                    .BuildNotification();

                AppNotificationManager.Default.Show(notification);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            NativeMethods.UnregisterHotKey(_hwnd, SaveLast45HotkeyId);

            if (_keyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            if (_oldWndProc != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC, _oldWndProc);
                _oldWndProc = IntPtr.Zero;
            }

            _newWndProc = null;
            _keyboardProc = null;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
