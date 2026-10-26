namespace SerialMonitor.WinUI.Infrastructure;

internal static class PercentageInput
{
    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var parsed = 0;
        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            // Saturate before the next digit to avoid overflow even for a huge paste.
            // Still validate the entire string: "999abc" must not become 100.
            parsed = Math.Min(100, parsed * 10 + character - '0');
        }

        value = parsed;
        return true;
    }
}
