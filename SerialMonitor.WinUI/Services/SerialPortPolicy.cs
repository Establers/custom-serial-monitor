using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public static class SerialPortPolicy
{
    public const int MaximumSupportedBaudRate = 921_600;
    public const int StartBitsPerCharacter = 1;
    public const int MinimumSupportedDataBits = 5;
    public const int MinimumSupportedParityBits = 0;
    public const int MinimumSupportedStopBits = 1;
    public const int MinimumSupportedBitsPerCharacter =
        StartBitsPerCharacter +
        MinimumSupportedDataBits +
        MinimumSupportedParityBits +
        MinimumSupportedStopBits;

    public static IReadOnlyList<int> SupportedBaudRates { get; } =
    [
        1200,
        4800,
        9600,
        19200,
        38400,
        57600,
        115200,
        230400,
        460800,
        MaximumSupportedBaudRate
    ];

    public static IReadOnlyList<int> SupportedDataBits { get; } =
    [
        5,
        6,
        7,
        8
    ];

    public static IReadOnlyList<SerialParityMode> SupportedParityModes { get; } =
    [
        SerialParityMode.None,
        SerialParityMode.Odd,
        SerialParityMode.Even,
        SerialParityMode.Mark,
        SerialParityMode.Space
    ];

    public static IReadOnlyList<SerialStopBitsMode> SupportedStopBitsModes { get; } =
    [
        SerialStopBitsMode.One,
        SerialStopBitsMode.OnePointFive,
        SerialStopBitsMode.Two
    ];

    public static double GetBitsPerCharacter(
        int dataBits,
        SerialParityMode parity,
        SerialStopBitsMode stopBits)
    {
        if (!SupportedDataBits.Contains(dataBits))
        {
            throw new ArgumentOutOfRangeException(nameof(dataBits));
        }

        if (!SupportedParityModes.Contains(parity))
        {
            throw new ArgumentOutOfRangeException(nameof(parity));
        }

        if (!SupportedStopBitsModes.Contains(stopBits))
        {
            throw new ArgumentOutOfRangeException(nameof(stopBits));
        }

        var parityBits = parity == SerialParityMode.None ? 0 : 1;
        var stopBitCount = stopBits switch
        {
            SerialStopBitsMode.One => 1d,
            SerialStopBitsMode.OnePointFive => 1.5d,
            SerialStopBitsMode.Two => 2d,
            _ => throw new ArgumentOutOfRangeException(nameof(stopBits))
        };

        return StartBitsPerCharacter + dataBits + parityBits + stopBitCount;
    }
}
