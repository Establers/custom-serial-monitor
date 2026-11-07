using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RJCP.IO.Ports.Serial;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal interface ISerialControlLinePort
{
    (SerialControlLineMode Dtr, SerialControlLineMode Rts) ReadModes();
    (bool Cts, bool Dsr, bool Dcd, bool Ri) ReadInputs();
    void WriteMode(SerialControlLine line, SerialControlLineMode mode);
    SerialPortConfiguration Configure(SerialPortConfiguration configuration) =>
        throw new NotSupportedException("Live serial configuration is unavailable for this transport.");
}

// RJCP 3.0.5 doesn't expose RTS_CONTROL_TOGGLE or its open handle publicly.
// Borrow its existing SafeFileHandle (never own, close, reopen or replace it).
// Keep this version-specific access isolated; the contract has a regression test.
// All Win32 calls use SafeHandle marshalling, including during a late close.
internal sealed class WindowsSerialControlLines(INativeSerial native) : ISerialControlLinePort
{
    private static readonly FieldInfo? HandleField = typeof(WinNativeSerial).GetField(
        "m_ComPortHandle", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static bool HasCompatibleHandleContract => HandleField?.FieldType == typeof(SafeFileHandle);

    private SafeFileHandle Handle => native is WinNativeSerial &&
        HandleField?.GetValue(native) is SafeFileHandle { IsClosed: false, IsInvalid: false } handle
            ? handle : throw new NotSupportedException("The serial library's open Windows handle is unavailable.");

    public (SerialControlLineMode Dtr, SerialControlLineMode Rts) ReadModes()
    {
        var state = ReadState(Handle);
        return DecodeModes(state.Flags);
    }

    internal void WriteBaudRate(int baudRate)
    {
        var handle = Handle;
        var state = ReadState(handle);
        state.BaudRate = checked((uint)baudRate);
        // RJCP's BaudRate setter rewrites all DCB flags and loses TX Toggle.
        // Change only the baud field on the existing handle instead.
        if (!SetCommState(handle, ref state)) ThrowDriverError("SetCommState (baud)");
    }

    public (bool Cts, bool Dsr, bool Dcd, bool Ri) ReadInputs()
    {
        if (!GetCommModemStatus(Handle, out var flags)) ThrowDriverError("GetCommModemStatus");
        return ((flags & 0x10) != 0, (flags & 0x20) != 0, (flags & 0x80) != 0, (flags & 0x40) != 0);
    }

    public void WriteMode(SerialControlLine line, SerialControlLineMode mode)
    {
        if (!Enum.IsDefined(line) || mode is < SerialControlLineMode.Off or > SerialControlLineMode.Toggle ||
            (line == SerialControlLine.Dtr && mode == SerialControlLineMode.Toggle))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var handle = Handle;
        var state = ReadState(handle);
        state.Flags = WithMode(state.Flags, line, mode);
        // Preserve every unrelated DCB field, timeouts, queues and the RX monitor.
        if (!SetCommState(handle, ref state)) ThrowDriverError("SetCommState");
        if (SerialControlLinePolicy.IsManual(mode))
        {
            var function = line == SerialControlLine.Dtr
                ? mode == SerialControlLineMode.On ? 5u : 6u
                : mode == SerialControlLineMode.On ? 3u : 4u;
            if (!EscapeCommFunction(handle, function)) ThrowDriverError("EscapeCommFunction");
        }
    }

    public SerialPortConfiguration Configure(SerialPortConfiguration configuration)
    {
        configuration.Validate();
        var handle = Handle;
        var before = ReadState(handle);
        var requested = before;
        requested.BaudRate = (uint)configuration.BaudRate;
        requested.ByteSize = (byte)configuration.DataBits;
        requested.Parity = (byte)configuration.Parity;
        requested.StopBits = (byte)configuration.StopBits;
        const uint changedFlags = (1u << 2) | (1u << 3) | (3u << 4) |
            (1u << 8) | (1u << 9) | (3u << 12);
        requested.Flags &= ~changedFlags;
        // Parity selects wire framing; fParity is the separate driver error-
        // reporting option. Preserve the port's existing error-reporting policy.
        if (SerialControlLinePolicy.UsesRtsHandshake(configuration.Handshake)) requested.Flags |= 1u << 2;
        if (SerialControlLinePolicy.UsesDtrHandshake(configuration.Handshake)) requested.Flags |= 1u << 3;
        if (configuration.Handshake is SerialHandshakeMode.XOn or SerialHandshakeMode.RtsXOn or
            SerialHandshakeMode.DtrXOn or SerialHandshakeMode.DtrRtsXOn) requested.Flags |= (1u << 8) | (1u << 9);
        requested.Flags = WithMode(WithMode(requested.Flags, SerialControlLine.Dtr, configuration.DtrMode),
            SerialControlLine.Rts, configuration.RtsMode);

        bool Matches(Dcb actual, Dcb expected) => actual.BaudRate == expected.BaudRate &&
            actual.ByteSize == expected.ByteSize && actual.Parity == expected.Parity &&
            actual.StopBits == expected.StopBits && actual.Flags == expected.Flags &&
            actual.XonLimit == expected.XonLimit && actual.XoffLimit == expected.XoffLimit &&
            actual.XonChar == expected.XonChar && actual.XoffChar == expected.XoffChar;
        void Write(Dcb state)
        {
            if (!SetCommState(handle, ref state)) ThrowDriverError("SetCommState (configuration)");
        }
        try
        {
            Write(requested);
            var actual = ReadState(handle);
            if (!Matches(actual, requested))
                throw new IOException($"The serial driver did not confirm the requested configuration. " +
                    $"Requested {requested.BaudRate}/{requested.ByteSize}/{requested.Parity}/{requested.StopBits}, flags 0x{requested.Flags:X}; " +
                    $"reported {actual.BaudRate}/{actual.ByteSize}/{actual.Parity}/{actual.StopBits}, flags 0x{actual.Flags:X}.");
            native.GetPortSettings(); // Refresh RJCP's cached framing without rewriting the DCB.
            return configuration;
        }
        catch (Exception error)
        {
            try
            {
                Write(before);
                if (!Matches(ReadState(handle), before)) throw new IOException("Previous serial configuration could not be verified.");
                native.GetPortSettings();
            }
            catch (Exception restoreError)
            {
                throw new SerialPortConfigurationException("Serial configuration failed and the previous state is unverified.",
                    false, new AggregateException(error, restoreError));
            }
            throw new SerialPortConfigurationException("Serial configuration failed; previous settings were restored. " + error.Message,
                true, error);
        }
    }

    internal static (SerialControlLineMode Dtr, SerialControlLineMode Rts) DecodeModes(uint flags)
    {
        var dtr = (flags >> 4) & 3;
        return (dtr <= 2 ? (SerialControlLineMode)dtr : SerialControlLineMode.Unknown,
            (SerialControlLineMode)((flags >> 12) & 3));
    }

    internal static uint WithMode(uint flags, SerialControlLine line, SerialControlLineMode mode)
    {
        var shift = line == SerialControlLine.Dtr ? 4 : 12;
        return (flags & ~(3u << shift)) | ((uint)mode << shift);
    }

    private static Dcb ReadState(SafeFileHandle handle)
    {
        var state = new Dcb { Length = (uint)Marshal.SizeOf<Dcb>() };
        if (!GetCommState(handle, ref state)) ThrowDriverError("GetCommState");
        return state;
    }

    private static void ThrowDriverError(string operation) => throw new Win32Exception(
        Marshal.GetLastWin32Error(), operation + " failed for the connected serial port.");

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint Length, BaudRate, Flags;
        public ushort Reserved, XonLimit, XoffLimit;
        public byte ByteSize, Parity, StopBits, XonChar, XoffChar, ErrorChar, EofChar, EventChar;
        public ushort Reserved1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(SafeFileHandle handle, ref Dcb state);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(SafeFileHandle handle, ref Dcb state);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommModemStatus(SafeFileHandle handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EscapeCommFunction(SafeFileHandle handle, uint function);
}

internal sealed class SerialPortConfigurationException(string message, bool stateRestored, Exception inner)
    : IOException(message, inner)
{
    internal bool StateRestored { get; } = stateRestored;
}
