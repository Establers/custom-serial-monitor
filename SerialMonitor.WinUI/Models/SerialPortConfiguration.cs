namespace SerialMonitor.WinUI.Models;

public sealed record SerialPortConfiguration(int BaudRate, int DataBits, SerialParityMode Parity,
    SerialStopBitsMode StopBits, SerialHandshakeMode Handshake, bool DtrEnable, bool RtsEnable,
    bool RtsToggleOnTransmit)
{
    public static SerialPortConfiguration FromSettings(SerialSettings settings) => new(settings.BaudRate,
        settings.DataBits, settings.Parity, settings.StopBits, settings.Handshake,
        settings.DtrEnable, settings.RtsEnable, settings.RtsToggleOnTransmit);

    public void Validate()
    {
        if (BaudRate is < 1 or > 10_000_000 || DataBits is < 5 or > 8 ||
            !Enum.IsDefined(Parity) || !Enum.IsDefined(StopBits) || !Enum.IsDefined(Handshake))
            throw new ArgumentException("Invalid serial port configuration.");
        if (RtsToggleOnTransmit && SerialControlLinePolicy.UsesRtsHandshake(Handshake))
            throw new ArgumentException("RTS TX Toggle cannot be combined with RTS/CTS handshaking.");
    }

    public void ApplyTo(SerialSettings settings)
    {
        settings.BaudRate = BaudRate;
        settings.DataBits = DataBits;
        settings.Parity = Parity;
        settings.StopBits = StopBits;
        settings.Handshake = Handshake;
        settings.DtrEnable = DtrEnable;
        settings.RtsEnable = RtsEnable;
        settings.RtsToggleOnTransmit = RtsToggleOnTransmit;
    }

    internal SerialControlLineMode DtrMode => SerialControlLinePolicy.UsesDtrHandshake(Handshake)
        ? SerialControlLineMode.Handshake : DtrEnable ? SerialControlLineMode.On : SerialControlLineMode.Off;
    internal SerialControlLineMode RtsMode => SerialControlLinePolicy.UsesRtsHandshake(Handshake)
        ? SerialControlLineMode.Handshake : RtsToggleOnTransmit ? SerialControlLineMode.Toggle
        : RtsEnable ? SerialControlLineMode.On : SerialControlLineMode.Off;
}
