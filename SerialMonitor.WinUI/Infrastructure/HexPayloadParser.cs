namespace SerialMonitor.WinUI.Infrastructure;

public static class HexPayloadParser
{
    public static bool TryParse(string? input, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        var tokens = (input ?? string.Empty)
            .Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            error = "HEX input is empty.";
            return false;
        }

        var result = new List<byte>();
        foreach (var rawToken in tokens)
        {
            var token = rawToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? rawToken[2..]
                : rawToken;

            if (token.Length == 0 || token.Length % 2 != 0)
            {
                error = "HEX input has an odd number of digits.";
                return false;
            }

            for (var index = 0; index < token.Length; index += 2)
            {
                if (!byte.TryParse(
                        token.AsSpan(index, 2),
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var value))
                {
                    error = "HEX input contains a non-hex digit.";
                    return false;
                }

                result.Add(value);
            }
        }

        bytes = result.ToArray();
        return bytes.Length > 0;
    }
}
