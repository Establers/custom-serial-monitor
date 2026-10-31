using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.WinUI.Infrastructure;

internal sealed record SequenceTimelinePart(string Text, bool Matched);
internal sealed record SequenceTimelineLine(string Time, string Direction, IReadOnlyList<SequenceTimelinePart> Parts);

internal static class SequenceTimelineFormatter
{
    public static IReadOnlyList<SequenceTimelineLine> Format(SequenceStepResult step,
        IReadOnlyList<SequenceRxSlice> slices, Encoding encoding, byte[]? decoderPrefix = null)
    {
        // Display the mode frozen with the executed step. A legacy expectation's
        // pattern kind must not override the actual TX mode.
        var hex = step.TxMode?.ToLowerInvariant() switch
        {
            "hex" => true,
            "terminal" or "text" => false,
            _ => string.Equals(step.Kind, "hex", StringComparison.OrdinalIgnoreCase)
        };
        var rows = new List<SequenceTimelineLine>();
        string Time(DateTimeOffset? at) => at?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "시간 기록 없음";
        string Tx(string? bytes, string? text) => hex ? SpaceHex(bytes ?? "") : (text ?? "");
        if (step.StepKind == "Tx")
            rows.Add(new(Time(step.StartedAt), step.SendStatus == "sent" ? "TX" : "TX?", [new(Tx(step.TxHex, step.Command), false)]));
        else if (step.RelatedTxAt is not null)
            rows.Add(new(Time(step.RelatedTxAt), "TX", [new(Tx(step.RelatedTxHex, step.RelatedTxCommand), false)]));
        var decoder = encoding.GetDecoder();
        if (decoderPrefix is { Length: > 0 }) decoder.GetChars(decoderPrefix, 0, decoderPrefix.Length, new char[encoding.GetMaxCharCount(decoderPrefix.Length)], 0, false);
        foreach (var slice in slices)
        {
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
                parts.Add(new(text, matched));
            }
            Add(0, start, false); Add(start, end - start, true); Add(end, slice.Bytes.Length - end, false);
            rows.Add(new(Time(slice.ReceivedAt), "RX", parts));
        }
        return rows;
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

    public static byte[] PendingBytes(byte[] bytes, Encoding encoding, byte[] prefix)
    {
        var decoder = encoding.GetDecoder();
        var pending = new List<byte>(4);
        Span<byte> input = stackalloc byte[1];
        Span<char> chars = stackalloc char[8];
        foreach (var value in prefix.Concat(bytes))
        {
            input[0] = value; pending.Add(value);
            if (decoder.GetChars(input, chars, false) > 0) pending.Clear();
            if (pending.Count > 4) pending.RemoveAt(0);
        }
        return pending.ToArray();
    }
}
