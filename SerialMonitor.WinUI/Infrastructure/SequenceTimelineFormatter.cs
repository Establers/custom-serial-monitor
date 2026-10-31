using System.Globalization;
using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

internal sealed record SequenceTimelinePart(string Text, bool Matched);
internal sealed record SequenceTimelineLine(string Time, string Direction, IReadOnlyList<SequenceTimelinePart> Parts);
internal sealed record SequenceLogDisplayOptions(RxLineEndingMode LineEnding = RxLineEndingMode.Auto,
    TimestampDisplayFormat TimestampFormat = TimestampDisplayFormat.DateTimeMilliseconds,
    bool ShowTimestamp = true, bool ShowDirection = true);

internal static class SequenceTimelineFormatter
{
    public static IReadOnlyList<SequenceTimelineLine> Format(SequenceStepResult step,
        IReadOnlyList<SequenceRxSlice> slices, Encoding encoding, byte[]? decoderPrefix = null,
        SequenceLogDisplayOptions? options = null, SequenceRxPageContinuation continuation = default)
    {
        options ??= new();
        // Display the mode frozen with the executed step. A legacy expectation's
        // pattern kind must not override the actual TX mode.
        var hex = step.TxMode?.ToLowerInvariant() switch
        {
            "hex" => true,
            "terminal" or "text" => false,
            _ => string.Equals(step.Kind, "hex", StringComparison.OrdinalIgnoreCase)
        };
        var rows = new List<SequenceTimelineLine>();
        string Time(DateTimeOffset? at) => at?.ToLocalTime().ToString(LogLine.GetTimestampFormatPattern(options.TimestampFormat), CultureInfo.InvariantCulture) ?? "시간 기록 없음";
        string Tx(string? bytes, string? text) => hex ? SpaceHex(bytes ?? "") : (text ?? "");
        if (step.StepKind == "Tx")
            rows.Add(new(Time(step.StartedAt), step.SendStatus == "sent" ? "TX" : "TX?", [new(Tx(step.TxHex, step.Command), false)]));
        else if (step.RelatedTxAt is not null)
            rows.Add(new(Time(step.RelatedTxAt), "TX", [new(Tx(step.RelatedTxHex, step.RelatedTxCommand), false)]));
        if (slices.Count > 0 && !continuation.CanContinue(slices[0]))
        {
            continuation = default;
            decoderPrefix = null;
        }
        var decoder = encoding.GetDecoder();
        if (decoderPrefix is { Length: > 0 }) decoder.GetChars(decoderPrefix, 0, decoderPrefix.Length, new char[encoding.GetMaxCharCount(decoderPrefix.Length)], 0, false);
        var pendingCrMatched = continuation.NextOffset is { } next && step.MatchStart is { } match &&
            next - 1 >= match && next - 1 < match + step.MatchLength;
        var terminal = new SequenceTerminalLineFormatter(options.LineEnding, Time, rows, continuation, pendingCrMatched);
        SequenceRxSlice? previous = null;
        foreach (var slice in slices)
        {
            if (!hex && previous is not null && !CanContinue(previous, slice))
            {
                terminal.EndInput();
                decoder.Reset();
            }
            var parts = new List<SequenceTimelinePart>();
            var start = (int)Math.Clamp((step.MatchStart ?? -1) - slice.Offset, 0, slice.Bytes.Length);
            var end = step.MatchStart is null ? start : (int)Math.Clamp(step.MatchStart.Value + step.MatchLength - slice.Offset, 0, slice.Bytes.Length);
            void Add(int offset, int length, bool matched)
            {
                if (length == 0) return;
                string text;
                if (hex) text = string.Join(" ", slice.Bytes.AsSpan(offset, length).ToArray().Select(b => b.ToString("X2"))) + " ";
                else
                {
                    // A receive chunk may end halfway through a UTF-8/CP949
                    // character. Retain decoder state across chunks and highlights.
                    var chars = new char[encoding.GetMaxCharCount(length)];
                    var count = decoder.GetChars(slice.Bytes, offset, length, chars, 0, false);
                    text = new string(chars, 0, count);
                }
                if (hex) parts.Add(new(text, matched));
                else terminal.Append(text, matched, slice.ReceivedAt);
            }
            Add(0, start, false); Add(start, end - start, true); Add(end, slice.Bytes.Length - end, false);
            if (hex) rows.Add(new(Time(slice.ReceivedAt), "RX", parts));
            previous = slice;
        }
        if (!hex) terminal.EndInput();
        return rows;
    }

    public static string FormatPrefix(SequenceTimelineLine line, SequenceLogDisplayOptions options)
    {
        var time = options.ShowTimestamp ? $"[{line.Time}] " : "";
        var direction = options.ShowDirection ? line.Direction switch
        {
            "TX" => "TX > ", "TX?" => "TX? > ", _ => "RX < "
        } : "";
        return time + direction;
    }

    private static string SpaceHex(string value)
    {
        var limit = Math.Min(value.Length, 32768);
        var count = 0;
        while (count < limit && char.IsAsciiHexDigit(value[count])) count++;
        var truncated = count < value.Length;
        value = value[..(count / 2 * 2)];
        var bytes = new List<string>();
        for (var i = 0; i < value.Length; i += 2) bytes.Add(value.Substring(i, Math.Min(2, value.Length - i)));
        return string.Join(" ", bytes) + (truncated ? " …" : "");
    }

    private static bool CanContinue(SequenceRxSlice previous, SequenceRxSlice current) =>
        previous.Generation == current.Generation && previous.Offset + previous.Bytes.Length == current.Offset;

    public static byte[] PendingBytes(byte[] bytes, Encoding encoding, byte[] prefix) =>
        PendingBytes(new SequenceRxSlice[] { new(0, bytes, null) }, encoding, prefix);

    public static byte[] PendingBytes(IReadOnlyList<SequenceRxSlice> slices, Encoding encoding, byte[] prefix)
    {
        var context = new TextDecoderContext(encoding);
        context.Observe(prefix);
        SequenceRxSlice? previous = null;
        foreach (var slice in slices)
        {
            if (previous is not null && !CanContinue(previous, slice))
            {
                // Page-tail calculation must use the same connection/offset
                // boundaries as rendering, including boundaries inside a page.
                context = new(encoding);
            }
            context.Observe(slice.Bytes);
            previous = slice;
        }
        return context.PendingBytes();
    }
}
