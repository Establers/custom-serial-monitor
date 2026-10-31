using System.Text;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// GUI, automation and direct text sends must agree on the actual wire bytes.
public static class TxPayloadEncoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static byte[] Encode(string command, TxSendMode mode, TxLineEndingMode ending)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (mode == TxSendMode.Hex)
        {
            if (!HexPayloadParser.TryParse(command, out var bytes, out var error))
                throw new ArgumentException(error, nameof(command));
            return bytes;
        }
        if (mode != TxSendMode.Terminal) throw new ArgumentOutOfRangeException(nameof(mode));
        var suffix = ending switch
        {
            TxLineEndingMode.None => "",
            TxLineEndingMode.Cr => "\r",
            TxLineEndingMode.Lf => "\n",
            TxLineEndingMode.Crlf => "\r\n",
            _ => throw new ArgumentOutOfRangeException(nameof(ending))
        };
        // Never silently replace an invalid UTF-16 command with EF BF BD.
        return StrictUtf8.GetBytes(command + suffix);
    }
}
