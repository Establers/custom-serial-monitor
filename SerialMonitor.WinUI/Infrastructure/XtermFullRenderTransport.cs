namespace SerialMonitor.WinUI.Infrastructure;

internal static class XtermFullRenderTransport
{
    public const int MaximumChunkCharacters = 64 * 1024;

    public static IEnumerable<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        for (var start = 0; start < text.Length;)
        {
            var end = GetChunkEnd(text, start);

            yield return text[start..end];
            start = end;
        }
    }

    public static int CountChunks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var count = 0;
        for (var start = 0; start < text.Length;)
        {
            count++;
            start = GetChunkEnd(text, start);
        }

        return count;
    }

    private static int GetChunkEnd(string text, int start)
    {
        var end = Math.Min(text.Length, start + MaximumChunkCharacters);
        if (end < text.Length)
        {
            var newline = text.LastIndexOf('\n', end - 1, end - start);
            if (newline >= start)
            {
                end = newline + 1;
            }
            else if (end > start && text[end - 1] == '\r' && text[end] == '\n')
            {
                end--;
            }
        }

        return end > start
            ? end
            : Math.Min(text.Length, start + MaximumChunkCharacters);
    }
}
