using System.Runtime.InteropServices;

namespace SerialMonitor.WinUI.Services;

public sealed class WindowsTrayNotifier : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifWarning = 0x00000002;
    private const uint NiifNoSound = 0x00000010;
    private const uint NotifyIconVersion4 = 4;
    private const int IdiApplication = 32512;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private const uint IconId = 1;
    private static readonly string AppIconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon", "SerialMonitor.ico");

    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private bool _isAdded;
    private bool _disposed;

    public bool Show(IntPtr windowHandle, string title, string message)
    {
        if (_disposed || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!_isAdded || _windowHandle != windowHandle)
        {
            RemoveIcon();
            _windowHandle = windowHandle;
            var addData = CreateData(windowHandle);
            addData.uFlags = NifIcon | NifTip;
            addData.hIcon = GetAppIcon();
            addData.szTip = "Serial Monitor";
            if (!Shell_NotifyIcon(NimAdd, ref addData))
            {
                _windowHandle = IntPtr.Zero;
                return false;
            }

            _isAdded = true;
            addData.uTimeoutOrVersion = NotifyIconVersion4;
            Shell_NotifyIcon(NimSetVersion, ref addData);
        }

        var notifyData = CreateData(windowHandle);
        notifyData.uFlags = NifInfo;
        notifyData.szInfoTitle = Truncate(title, 63);
        notifyData.szInfo = Truncate(message, 255);
        notifyData.dwInfoFlags = NiifWarning | NiifNoSound;
        return Shell_NotifyIcon(NimModify, ref notifyData);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveIcon();
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }

    public void Hide()
    {
        if (!_disposed)
        {
            RemoveIcon();
        }
    }

    private void RemoveIcon()
    {
        if (!_isAdded || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateData(_windowHandle);
        Shell_NotifyIcon(NimDelete, ref data);
        _isAdded = false;
        _windowHandle = IntPtr.Zero;
    }

    private static NotifyIconData CreateData(IntPtr windowHandle)
    {
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = windowHandle,
            uID = IconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private IntPtr GetAppIcon()
    {
        if (_iconHandle != IntPtr.Zero)
        {
            return _iconHandle;
        }

        if (File.Exists(AppIconPath))
        {
            _iconHandle = LoadImage(
                IntPtr.Zero,
                AppIconPath,
                ImageIcon,
                0,
                0,
                LrLoadFromFile | LrDefaultSize);
        }

        return _iconHandle != IntPtr.Zero
            ? _iconHandle
            : LoadIcon(IntPtr.Zero, (IntPtr)IdiApplication);
    }

    private static string Truncate(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "Serial event"
            : value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
