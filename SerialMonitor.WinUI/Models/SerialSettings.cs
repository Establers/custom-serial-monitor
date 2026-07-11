namespace SerialMonitor.WinUI.Models;

public enum RxLineEndingMode
{
    Auto,
    Cr,
    Lf,
    Crlf
}

public enum TxLineEndingMode
{
    None,
    Cr,
    Lf,
    Crlf
}

public enum RxEncodingMode
{
    Ascii,
    Utf8,
    Cp949,
    Hex
}

public enum SerialParityMode
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum SerialStopBitsMode
{
    One,
    OnePointFive,
    Two
}

public enum SerialHandshakeMode
{
    None,
    XOn,
    Rts,
    Dtr,
    RtsXOn,
    DtrXOn,
    DtrRts,
    DtrRtsXOn
}

public enum SerialConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Faulted
}

public sealed class SerialSettings
{
    public string PortName { get; set; } = "MOCK";

    public int BaudRate { get; set; } = 115200;

    public int DataBits { get; set; } = 8;

    public SerialParityMode Parity { get; set; } = SerialParityMode.None;

    public SerialStopBitsMode StopBits { get; set; } = SerialStopBitsMode.One;

    public SerialHandshakeMode Handshake { get; set; } = SerialHandshakeMode.None;

    public bool DtrEnable { get; set; }

    public bool RtsEnable { get; set; }

    public RxLineEndingMode RxLineEnding { get; set; } = RxLineEndingMode.Auto;

    public TxLineEndingMode TxLineEnding { get; set; } = TxLineEndingMode.Crlf;

    public RxEncodingMode Encoding { get; set; } = RxEncodingMode.Utf8;

    public string SaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "logs");

    public SerialSettings Clone()
    {
        return new SerialSettings
        {
            PortName = PortName,
            BaudRate = BaudRate,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            Handshake = Handshake,
            DtrEnable = DtrEnable,
            RtsEnable = RtsEnable,
            RxLineEnding = RxLineEnding,
            TxLineEnding = TxLineEnding,
            Encoding = Encoding,
            SaveDirectory = SaveDirectory
        };
    }
}
