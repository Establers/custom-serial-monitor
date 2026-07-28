using System.Text;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class XtermLogTextBatchCoalescer
{
    public static LogTextBatch[] Coalesce(
        IEnumerable<LogTextBatch> batches,
        int maxLineCount,
        int maxCharacterCount)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (maxLineCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineCount));
        }

        if (maxCharacterCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacterCount));
        }

        var result = new List<LogTextBatch>();
        StringBuilder? text = null;
        var lineCount = 0;
        var trimCharacterCount = 0;
        long endDisplayedLineCount = 0;

        foreach (var batch in batches)
        {
            ArgumentNullException.ThrowIfNull(batch);

            var wouldExceedLimit = text is not null &&
                ((long)lineCount + batch.LineCount > maxLineCount ||
                 (long)text.Length + batch.AppendedText.Length > maxCharacterCount);
            if (wouldExceedLimit)
            {
                result.Add(new LogTextBatch(
                    text!.ToString(),
                    trimCharacterCount,
                    lineCount,
                    endDisplayedLineCount));
                text = null;
                lineCount = 0;
                trimCharacterCount = 0;
            }

            text ??= new StringBuilder(Math.Min(maxCharacterCount, batch.AppendedText.Length));
            text.Append(batch.AppendedText);
            lineCount += batch.LineCount;
            trimCharacterCount += batch.TrimCharacterCount;
            endDisplayedLineCount = batch.EndDisplayedLineCount;
        }

        if (text is not null)
        {
            result.Add(new LogTextBatch(
                text.ToString(),
                trimCharacterCount,
                lineCount,
                endDisplayedLineCount));
        }

        return result.ToArray();
    }
}
