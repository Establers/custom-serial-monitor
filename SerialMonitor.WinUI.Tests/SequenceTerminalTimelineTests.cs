using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceTerminalTimelineTests
{
    private static readonly DateTimeOffset TxAt = new(2026, 10, 31, 17, 20, 9, 448, TimeSpan.FromHours(9));
    private static string Text(SequenceTimelineLine line) => string.Concat(line.Parts.Select(part => part.Text));
    private static SequenceStepResult Step(long? match = null, int length = 0) => new(1, 1, 2, 1,
        "ID?", "49443F0D0A", "not_requested", "matched", "NUCLEO", "text", 100,
        TxAt, TxAt.AddMilliseconds(100), 2, 0, 100, 100, match, length, null, StepKind: "RxCheck",
        RelatedTxAt: TxAt, RelatedTxCommand: "ID?", TxMode: "Terminal");

    [Fact]
    public void DeviceIdReplyMatchesTheTerminalInsteadOfShowingDriverReadFragments()
    {
        var fragments = new[] { "ID NUCLEO-F", "103RB STM32", "F103RB SERIA", "L-LAB/1\r\n" };
        long offset = 0;
        var slices = fragments.Select((text, i) =>
        {
            var slice = new SequenceRxSlice(offset, Encoding.UTF8.GetBytes(text), TxAt.AddMilliseconds(i + 2));
            offset += slice.Bytes.Length;
            return slice;
        }).ToArray();
        var options = new SequenceLogDisplayOptions(TimestampFormat: TimestampDisplayFormat.ShortDateTimeMilliseconds,
            ShowDirection: false);
        var rows = SequenceTimelineFormatter.Format(Step(3, 10), slices, Encoding.UTF8, options: options);

        Assert.Equal(new[] { "TX", "RX" }, rows.Select(row => row.Direction));
        Assert.Equal("ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1", Text(rows[1]));
        Assert.Equal("NUCLEO-F10", string.Concat(rows[1].Parts.Where(part => part.Matched).Select(part => part.Text)));
        var expected = new LogLine(TxAt.AddMilliseconds(5), LogDirection.Rx, Text(rows[1]));
        Assert.Equal($"[{expected.FormatTimestamp(options.TimestampFormat)}] {expected.Text}",
            SequenceTimelineFormatter.FormatPrefix(rows[1], options) + Text(rows[1]));
        Assert.Equal($"[{new LogLine(TxAt, LogDirection.Tx, "ID?").FormatTimestamp(options.TimestampFormat)}] ID?",
            SequenceTimelineFormatter.FormatPrefix(rows[0], options) + Text(rows[0]));
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("ks_c_5601-1987")]
    public void EveryReadSplitPreservesLogicalLinesAndMatchedKoreanText(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(name);
        var bytes = encoding.GetBytes("장치 준비\r\n다음\n");
        var start = encoding.GetByteCount("장치 ");
        for (var split = 1; split < bytes.Length; split++)
        {
            var lines = SequenceTimelineFormatter.Format(Step(start, encoding.GetByteCount("준비")),
                [new(0, bytes[..split], TxAt), new(split, bytes[split..], TxAt.AddMilliseconds(5))], encoding);
            Assert.Equal(new[] { "ID?", "장치 준비", "다음" }, lines.Select(Text));
            Assert.Equal("준비", string.Concat(lines.SelectMany(line => line.Parts)
                .Where(part => part.Matched).Select(part => part.Text)));
        }
    }

    [Theory]
    [InlineData(RxLineEndingMode.Auto, "A\r\n\r\nB\rC\nD")]
    [InlineData(RxLineEndingMode.Cr, "A\r\rB\nC\rD")]
    [InlineData(RxLineEndingMode.Lf, "A\n\nB\rC\nD")]
    [InlineData(RxLineEndingMode.Crlf, "A\r\n\r\nB\rC\nD\r")]
    public void RecordedTextUsesTheSameLineEndingsAsTheMainTerminal(RxLineEndingMode mode, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var parser = new LineParser();
        var expected = parser.Append(bytes, mode).Select(line => Encoding.UTF8.GetString(line.Bytes)).ToList();
        if (parser.FlushPartial(mode) is { } tail) expected.Add(Encoding.UTF8.GetString(tail.Bytes));
        var slices = bytes.Select((value, i) => new SequenceRxSlice(i, [value], TxAt.AddMilliseconds(i))).ToArray();

        var rows = SequenceTimelineFormatter.Format(Step(), slices, Encoding.UTF8,
            options: new(LineEnding: mode));

        Assert.Equal(expected, rows.Where(row => row.Direction == "RX").Select(Text));
    }

    [Fact]
    public void OneReadContainingMultipleLinesUsesItsReceiveTimeForEachLine()
    {
        var at = TxAt.AddMilliseconds(5);
        var rows = SequenceTimelineFormatter.Format(Step(),
            [new(0, "first\r\nsecond\n\ntail"u8.ToArray(), at)], Encoding.UTF8);
        var rx = rows.Where(row => row.Direction == "RX").ToArray();

        Assert.Equal(new[] { "first", "second", "", "tail" }, rx.Select(Text));
        Assert.All(rx, row => Assert.Equal(rows[1].Time, row.Time));
        Assert.NotEqual(rows[0].Time, rows[1].Time);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconnectsAndMissingBytesCannotJoinUnrelatedText(bool reconnect)
    {
        var rows = SequenceTimelineFormatter.Format(Step(),
            [new(0, "before"u8.ToArray(), TxAt, Generation: 1),
             new(reconnect ? 6 : 9, "after\n"u8.ToArray(), TxAt.AddMilliseconds(5), Generation: reconnect ? 2 : 1)], Encoding.UTF8);

        Assert.Equal(new[] { "ID?", "before", "after" }, rows.Select(Text));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void PrefixFollowsTheMainLogTimestampAndDirectionSettings(bool timestamp, bool direction)
    {
        var options = new SequenceLogDisplayOptions(TimestampFormat: TimestampDisplayFormat.TimeMilliseconds,
            ShowTimestamp: timestamp, ShowDirection: direction);
        var rows = SequenceTimelineFormatter.Format(Step(), [new(0, "ID\n"u8.ToArray(), TxAt)], Encoding.UTF8, options: options);
        for (var i = 0; i < rows.Count; i++)
        {
            var mainLog = new LogLine(TxAt, i == 0 ? LogDirection.Tx : LogDirection.Rx, Text(rows[i]));
            var expected = (timestamp ? $"[{mainLog.FormatTimestamp(options.TimestampFormat)}] " : "") +
                (direction ? mainLog.DirectionText + " " : "");
            Assert.Equal(expected, SequenceTimelineFormatter.FormatPrefix(rows[i], options));
        }
    }
}
