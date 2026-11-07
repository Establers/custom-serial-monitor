namespace SerialMonitor.WinUI.Models;

public enum SerialControlLine { Dtr, Rts }

// Values 0..3 match the Windows DCB control modes. These describe the driver
// configuration, not an electrical measurement of the output pin.
public enum SerialControlLineMode { Unknown = -1, Off, On, Handshake, Toggle }
public enum SerialModemStatusKind { Disconnected, Available, Unsupported, Error, Mock }

public sealed record SerialControlLineStatus(
    long Generation,
    SerialControlLineMode Dtr,
    SerialControlLineMode Rts,
    SerialModemStatusKind InputStatus,
    bool? Cts = null, bool? Dsr = null, bool? Dcd = null, bool? Ri = null,
    string? Error = null)
{
    public static SerialControlLineStatus Disconnected { get; } = new(0,
        SerialControlLineMode.Unknown, SerialControlLineMode.Unknown, SerialModemStatusKind.Disconnected);
}

internal static class SerialControlLinePolicy
{
    internal static bool UsesDtrHandshake(SerialHandshakeMode mode) => mode is
        SerialHandshakeMode.Dtr or SerialHandshakeMode.DtrXOn or
        SerialHandshakeMode.DtrRts or SerialHandshakeMode.DtrRtsXOn;

    internal static bool UsesRtsHandshake(SerialHandshakeMode mode) => mode is
        SerialHandshakeMode.Rts or SerialHandshakeMode.RtsXOn or
        SerialHandshakeMode.DtrRts or SerialHandshakeMode.DtrRtsXOn;

    internal static bool IsManual(SerialControlLineMode mode) => mode is
        SerialControlLineMode.Off or SerialControlLineMode.On;
}
