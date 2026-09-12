namespace SerialMonitor.WinUI.Infrastructure;

public static class HexFormatter
{
    private const string Digits = "0123456789ABCDEF";

    public static string Format(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;
        return string.Create(checked(bytes.Length * 3 - 1), bytes, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var offset = i * 3;
                if (i > 0) destination[offset - 1] = ' ';
                destination[offset] = Digits[source[i] >> 4];
                destination[offset + 1] = Digits[source[i] & 15];
            }
        });
    }

    public static string Format(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        // A span cannot be string.Create's state on .NET 8. Keep only two
        // bulk strings instead of allocating a string for every byte.
        var compact = Convert.ToHexString(bytes);
        return string.Create(checked(bytes.Length * 3 - 1), compact, static (destination, source) =>
        {
            for (var i = 0; i < source.Length / 2; i++)
            {
                var offset = i * 3;
                if (i > 0) destination[offset - 1] = ' ';
                destination[offset] = source[i * 2];
                destination[offset + 1] = source[i * 2 + 1];
            }
        });
    }
}
