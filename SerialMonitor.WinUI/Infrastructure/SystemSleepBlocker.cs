using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SerialMonitor.WinUI.Infrastructure;

internal sealed class SystemSleepBlocker : IDisposable
{
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private readonly object _gate = new();
    private SafeFileHandle? _requestHandle;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _requestHandle is { IsInvalid: false, IsClosed: false };
            }
        }
    }

    public void Acquire(string reason)
    {
        lock (_gate)
        {
            if (_requestHandle is { IsInvalid: false, IsClosed: false })
            {
                return;
            }

            var reasonPointer = Marshal.StringToHGlobalUni(reason);
            try
            {
                var context = new PowerRequestContext
                {
                    Version = PowerRequestContextVersion,
                    Flags = PowerRequestContextSimpleString,
                    ReasonString = reasonPointer
                };
                var handle = PowerCreateRequest(ref context);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error, "Windows could not create the serial-monitoring power request.");
                }

                if (!PowerSetRequest(handle, PowerRequestType.SystemRequired))
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error, "Windows could not prevent automatic sleep for serial monitoring.");
                }

                _requestHandle = handle;
            }
            finally
            {
                Marshal.FreeHGlobal(reasonPointer);
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            ReleaseCore();
        }
    }

    public void ReleaseIfDisconnected(Func<bool> shouldRemainActive)
    {
        ArgumentNullException.ThrowIfNull(shouldRemainActive);
        lock (_gate)
        {
            if (!shouldRemainActive())
            {
                ReleaseCore();
            }
        }
    }

    public void Dispose() => Release();

    private void ReleaseCore()
    {
        var handle = _requestHandle;
        _requestHandle = null;
        if (handle is null)
        {
            return;
        }

        if (!handle.IsInvalid && !handle.IsClosed)
        {
            PowerClearRequest(handle, PowerRequestType.SystemRequired);
        }

        handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerRequestContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr ReasonString;
    }

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref PowerRequestContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle powerRequest, PowerRequestType requestType);
}
