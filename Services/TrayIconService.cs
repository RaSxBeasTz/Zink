using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Zink.Services
{
    internal sealed class TrayIconService : IDisposable
    {
        public event EventHandler? OpenClicked;
        public event EventHandler? SaveLast45SecondsClicked;
        public event EventHandler? ExitClicked;

        private const int WM_APP = 0x8000;
        private const int WM_TRAYICON = WM_APP + 1;

        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_COMMAND = 0x0111;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;
        private const uint NIF_SHOWTIP = 0x00000080;

        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_BOTTOMALIGN = 0x0020;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;

        private const int ID_OPEN = 1001;
        private const int ID_SAVE = 1002;
        private const int ID_EXIT = 1003;

        private NativeWindow? _window;
        private NOTIFYICONDATA _nid;
        private IntPtr _iconHandle = IntPtr.Zero;
        private bool _created;
        private bool _disposed;

        public bool IsCreated => _created;

        public void Create()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TrayIconService));

            if (_created)
                return;

            _window = new NativeWindow();
            _window.MessageReceived += OnWindowMessage;

            _iconHandle = LoadTrayIcon();

            _nid = new NOTIFYICONDATA();
            _nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
            _nid.hWnd = _window.Handle;
            _nid.uID = 1;
            _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            _nid.uCallbackMessage = WM_TRAYICON;
            _nid.hIcon = _iconHandle;
            _nid.szTip = "Zink";

            if (!Shell_NotifyIcon(NIM_ADD, ref _nid))
                throw new InvalidOperationException("Failed to create tray icon.");

            _created = true;
        }

        public void ShowBalloonTip(string title, string text, int timeoutMs = 3000)
        {
            if (!_created)
                return;

            _nid.uFlags = NIF_INFO;
            _nid.uTimeoutOrVersion = (uint)timeoutMs;
            _nid.szInfoTitle = title ?? "";
            _nid.szInfo = text ?? "";

            Shell_NotifyIcon(NIM_MODIFY, ref _nid);

            _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            _nid.szInfoTitle = "";
            _nid.szInfo = "";
        }

        public void SetVisible(bool visible)
        {
            if (!_created)
                return;

            if (visible)
            {
                Shell_NotifyIcon(NIM_ADD, ref _nid);
            }
            else
            {
                Shell_NotifyIcon(NIM_DELETE, ref _nid);
            }
        }

        private IntPtr OnWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAYICON)
            {
                uint mouseMsg = unchecked((uint)lParam.ToInt64());

                if (mouseMsg == WM_LBUTTONDBLCLK)
                {
                    OpenClicked?.Invoke(this, EventArgs.Empty);
                    return new IntPtr(1);
                }

                if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowContextMenu(hWnd);
                    return new IntPtr(1);
                }
            }

            if (msg == WM_COMMAND)
            {
                int commandId = LOWORD(wParam);

                switch (commandId)
                {
                    case ID_OPEN:
                        OpenClicked?.Invoke(this, EventArgs.Empty);
                        return new IntPtr(1);

                    case ID_SAVE:
                        SaveLast45SecondsClicked?.Invoke(this, EventArgs.Empty);
                        return new IntPtr(1);

                    case ID_EXIT:
                        ExitClicked?.Invoke(this, EventArgs.Empty);
                        return new IntPtr(1);
                }
            }

            return IntPtr.Zero;
        }

        private void ShowContextMenu(IntPtr hWnd)
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return;

            try
            {
                AppendMenu(menu, MF_STRING, ID_OPEN, "Open Zink");
                int replaySeconds = Recording.RecordingPreferences.BackgroundReplayDurationSeconds;
                string replayLength = replaySeconds < 60
                    ? $"{replaySeconds} seconds"
                    : $"{replaySeconds / 60} minute{(replaySeconds == 60 ? "" : "s")}";
                AppendMenu(menu, MF_STRING, ID_SAVE, $"Save the last {replayLength}");
                AppendMenu(menu, MF_SEPARATOR, 0, null);
                AppendMenu(menu, MF_STRING, ID_EXIT, "Exit");

                GetCursorPos(out POINT pt);
                SetForegroundWindow(hWnd);

                int commandId = TrackPopupMenu(
                    menu,
                    TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD,
                    pt.X,
                    pt.Y,
                    0,
                    hWnd,
                    IntPtr.Zero);

                InvokeCommand(commandId);
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        private void InvokeCommand(int commandId)
        {
            switch (commandId)
            {
                case ID_OPEN:
                    OpenClicked?.Invoke(this, EventArgs.Empty);
                    break;

                case ID_SAVE:
                    SaveLast45SecondsClicked?.Invoke(this, EventArgs.Empty);
                    break;

                case ID_EXIT:
                    ExitClicked?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        private IntPtr LoadTrayIcon()
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Zink.ico");

            if (File.Exists(iconPath))
            {
                IntPtr hIcon = LoadImage(
                    IntPtr.Zero,
                    iconPath,
                    IMAGE_ICON,
                    0,
                    0,
                    LR_LOADFROMFILE | LR_DEFAULTSIZE);

                if (hIcon != IntPtr.Zero)
                    return hIcon;
            }

            return LoadIcon(IntPtr.Zero, (IntPtr)32512);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (_created)
                {
                    Shell_NotifyIcon(NIM_DELETE, ref _nid);
                    _created = false;
                }
            }
            catch
            {
            }

            try
            {
                if (_iconHandle != IntPtr.Zero)
                {
                    DestroyIcon(_iconHandle);
                    _iconHandle = IntPtr.Zero;
                }
            }
            catch
            {
            }

            try
            {
                if (_window != null)
                {
                    _window.MessageReceived -= OnWindowMessage;
                    _window.Dispose();
                    _window = null;
                }
            }
            catch
            {
            }
        }

        private static int LOWORD(IntPtr value)
        {
            return unchecked((short)((long)value & 0xFFFF));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;

            public uint dwState;
            public uint dwStateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;

            public uint uTimeoutOrVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;

            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(
            IntPtr hInst,
            string lpszName,
            uint uType,
            int cxDesired,
            int cyDesired,
            uint fuLoad);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(
            IntPtr hMenu,
            uint uFlags,
            int x,
            int y,
            int nReserved,
            IntPtr hWnd,
            IntPtr prcRect);
    }
}
