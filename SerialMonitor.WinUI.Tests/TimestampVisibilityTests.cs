using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TimestampVisibilityTests
{
    [Theory]
    [InlineData(RxDisplayMode.Terminal, false)]
    [InlineData(RxDisplayMode.Terminal, true)]
    [InlineData(RxDisplayMode.Hex, false)]
    [InlineData(RxDisplayMode.Hex, true)]
    public void Toggle_RebuildsExistingLogsAndNotifiesXtermWithoutNewData(
        RxDisplayMode mode, bool showDirection)
    {
        var model = new LogViewModel(100);
        model.SetRxDisplayMode(mode);
        model.SetShowRxTxDirectionPrefixInLogView(showDirection);
        var timestamp = DateTimeOffset.Now;
        model.AddRange([
            new LogLine(timestamp, LogDirection.Rx, "READY", "READY"u8.ToArray()),
            new LogLine(timestamp.AddSeconds(1), LogDirection.Tx, "PING", "PING"u8.ToArray())
        ]);
        var originalText = model.GetVisibleTextSnapshot();
        var originalXtermText = model.GetXtermTextSnapshot();
        var originalLines = model.GetVisibleSearchContentSnapshot();
        var rebuiltSnapshots = new List<string>();
        model.TextRebuilt += (_, _) => rebuiltSnapshots.Add(model.GetXtermTextSnapshot());

        model.SetShowTimestampInLogView(false);

        var hiddenSnapshot = Assert.Single(rebuiltSnapshots);
        for (var index = 0; index < originalLines.Count; index++)
        {
            var original = originalLines[index];
            var hidden = model.GetVisibleSearchContentSnapshot()[index];
            Assert.Equal(original.LineId, hidden.LineId);
            Assert.Equal(original.TimestampUnixMilliseconds, hidden.TimestampUnixMilliseconds);
            Assert.Equal(original.Direction, hidden.Direction);
            Assert.Equal(original.PayloadText, hidden.PayloadText);
            Assert.Equal(showDirection ? 5 : 0, hidden.PayloadStart);
            Assert.Contains(hidden.FullText, hiddenSnapshot);
            Assert.DoesNotContain("[" + timestamp.ToLocalTime().ToString("yyyy-MM-dd"), hiddenSnapshot);
        }
        Assert.Equal(2, model.TotalRetainedLineCount);
        Assert.Equal(2, model.DisplayedLineCount);

        model.SetShowTimestampInLogView(true);

        Assert.Equal(2, rebuiltSnapshots.Count);
        Assert.Equal(originalText, model.GetVisibleTextSnapshot());
        Assert.Equal(originalXtermText, rebuiltSnapshots[1]);
    }

    [Fact]
    public void Toggle_DuringPartialRxPreservesMergedPayloadAndContinuedReception()
    {
        var model = new LogViewModel(100);
        var timestamp = DateTimeOffset.Now;
        model.AddRange([new LogLine(timestamp, LogDirection.Rx, "REA", isPartialRxSegment: true)]);

        model.SetShowTimestampInLogView(false);
        model.AddRange([new LogLine(timestamp.AddMilliseconds(50), LogDirection.Rx, "DY", isPartialRxSegment: true)]);

        Assert.Equal("RX < READY", Assert.Single(model.GetVisibleSearchContentSnapshot()).FullText);

        model.SetShowTimestampInLogView(true);
        model.AddRange([new LogLine(timestamp.AddMilliseconds(100), LogDirection.Rx, "!", isPartialRxSegment: true)]);
        model.AddRange([new LogLine(timestamp.AddMilliseconds(150), LogDirection.Rx, string.Empty, isPartialRxTerminator: true)]);

        var line = Assert.Single(model.GetVisibleSearchContentSnapshot());
        Assert.Equal("READY!", line.PayloadText);
        Assert.Equal(1, line.LineId);
        Assert.Equal(timestamp.ToUnixTimeMilliseconds(), line.TimestampUnixMilliseconds);
        Assert.StartsWith("[", line.FullText);
    }

    [Fact]
    public void UnchangedSetting_DoesNotRequestAnotherXtermRebuild()
    {
        var model = new LogViewModel(100);
        model.AddRange([LogLine.Rx("READY")]);
        var rebuilds = 0;
        model.TextRebuilt += (_, _) => rebuilds++;

        model.SetShowTimestampInLogView(true);
        Assert.Equal(0, rebuilds);

        model.SetShowTimestampInLogView(false);
        model.SetShowTimestampInLogView(false);
        Assert.Equal(1, rebuilds);
    }
}
