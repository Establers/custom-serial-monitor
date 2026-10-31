using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// These limits apply only to the screen projection. Serial RX, rule evaluation,
// sequence evidence and the live file writer keep their original LogLine/bytes.
internal sealed record LogViewMemoryPolicy(
    long RetainedBytes = 64L * 1024 * 1024,
    long VisibleCharacters = 16L * 1024 * 1024,
    int ContinuousLineCharacters = 1024 * 1024,
    int PreviewCharacters = 1024 * 1024 + 256,
    int HexPreviewBytes = 1024 * 1024)
{
    internal static LogViewMemoryPolicy Default { get; } = new();
    internal const long PendingBytes = 32L * 1024 * 1024;
    internal const string PreviewSuffix = " … [preview]";

    // A real RX completion is at most SerialService's 1 MiB transport buffer;
    // Terminal parsing may carry fewer than 128 bytes from an earlier chunk.
    // Ordinary incoming records therefore remain complete on screen. Preview
    // guards only oversized synthetic/TX records, not normal serial packets.

    internal static long EstimateRetainedBytes(LogLine line)
    {
        var size = 256L + (line.RawBytes?.LongLength ?? 0) + 2L * line.Text.Length;
        if (!ReferenceEquals(line.Text, line.DisplayText)) size += 2L * line.DisplayText.Length;
        if (!ReferenceEquals(line.Text, line.FileText) && !ReferenceEquals(line.DisplayText, line.FileText))
            size += 2L * line.FileText.Length;
        return size;
    }

    internal string Preview(string text)
    {
        if (text.Length <= PreviewCharacters) return text;
        var length = PreviewCharacters;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + PreviewSuffix;
    }

    internal string HexPreview(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;
        return bytes.Length <= HexPreviewBytes ? HexFormatter.Format(bytes)
            : HexFormatter.Format(bytes.AsSpan(0, HexPreviewBytes)) + PreviewSuffix;
    }
}
