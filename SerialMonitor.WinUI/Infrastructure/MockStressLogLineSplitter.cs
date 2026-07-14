using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class MockStressLogLineSplitter
{
    public static IEnumerable<string> Split(LogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.ContentMode != LogRuleMatchMode.Hex)
        {
            yield return line.Text;
            yield break;
        }

        var text = line.Text;
        var lineStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            if (index > lineStart)
            {
                yield return text[lineStart..index];
            }

            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            lineStart = index + 1;
        }

        if (lineStart < text.Length)
        {
            yield return text[lineStart..];
        }
    }
}
