using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProtocolTimelineTests
{
    private static readonly byte[] Request = Convert.FromHexString("010300000002C40B");
    private static readonly byte[] ExceptionResponse = Convert.FromHexString("018302C0F1");
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 22, 6, 33, 707, TimeSpan.FromHours(9));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestAndResponse_AreChronologicalRegardlessOfAssemblyBoundary(bool nativeBoundary)
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        var model = CreateModel(analyzer);
        Assert.True(analyzer.TryEnqueue(Request, ProtocolDirection.Tx, 1, Start));
        Assert.True(analyzer.TryEnqueue(ExceptionResponse, ProtocolDirection.Rx, 1, Start.AddMilliseconds(7),
            new ProtocolInputTiming(0, nativeBoundary, 40, 1.75)));
        await Wait(analyzer, 2);
        model.Refresh();

        Assert.Equal(new[] { ProtocolDirection.Tx, ProtocolDirection.Rx }, model.Frames.Select(row => row.Frame.Direction));
        Assert.Equal(new[] { Start, Start.AddMilliseconds(7) }, model.Frames.Select(row => row.Frame.Timestamp));
        Assert.Equal(ModbusFrameKind.Exception, ((ModbusRtuFrame)model.Frames[1].Frame).Kind);
    }

    [Fact]
    public async Task LateOlderFrame_IsInsertedBeforeSelection_AndPauseFiltersAndTabRestoreKeepTimeline()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        var model = CreateModel(analyzer);
        await Push(analyzer, ProtocolDirection.Rx, Start.AddMilliseconds(7));
        model.Refresh();
        var selected = Assert.Single(model.Frames);
        model.SelectedFrame = selected;
        await Push(analyzer, ProtocolDirection.Tx, Start);
        model.Refresh();
        Assert.Equal(new long[] { 2, 1 }, model.Frames.Select(row => row.Id));
        Assert.Same(selected, model.SelectedFrame);
        Assert.False(model.IsFollowingLatest);

        // A newer ID need not be the last row: equal timestamps use IDs as a
        // stable tie-breaker, and an incremental refresh must not duplicate it.
        await Push(analyzer, ProtocolDirection.Tx, Start);
        model.Refresh();
        Assert.Equal(new long[] { 2, 3, 1 }, model.Frames.Select(row => row.Id));
        model.Refresh(force: true);
        Assert.Equal(new long[] { 2, 3, 1 }, model.Frames.Select(row => row.Id));
        Assert.Same(selected, model.SelectedFrame);

        model.IsDisplayPaused = true;
        await Push(analyzer, ProtocolDirection.Rx, Start.AddMilliseconds(14));
        model.Refresh();
        Assert.Equal(1, model.PendingDisplayFrames);
        model.IsVisible = false;
        model.IsVisible = true;
        Assert.Equal(new long[] { 2, 3, 1 }, model.Frames.Select(row => row.Id));
        Assert.Equal(selected.Id, model.SelectedFrame!.Id);
        model.FilterDirectionIndex = 1; // RX only, from the frozen snapshot.
        Assert.True(model.TryApplyFilters());
        Assert.Equal(selected.Id, Assert.Single(model.Frames).Id);
        model.ResetFilters();
        model.IsDisplayPaused = false;
        Assert.Equal(new long[] { 2, 3, 1, 4 }, model.Frames.Select(row => row.Id));
        Assert.Equal(selected.Id, model.SelectedFrame!.Id);
    }

    [Fact]
    public async Task CompletionOrderEviction_RemovesRowsAnywhereInChronologicalList_AndFollowingContinues()
    {
        await using var analyzer = new ModbusRtuAnalyzer(historyCapacity: 2);
        var model = CreateModel(analyzer);
        var latestRequests = 0;
        model.LatestRequested += (_, _) => latestRequests++;
        await Push(analyzer, ProtocolDirection.Rx, Start.AddMilliseconds(7));
        await Push(analyzer, ProtocolDirection.Tx, Start);
        model.Refresh();
        Assert.Equal(new long[] { 2, 1 }, model.Frames.Select(row => row.Id));

        await Push(analyzer, ProtocolDirection.Rx, Start.AddMilliseconds(14));
        model.Refresh();
        Assert.Equal(new long[] { 2, 3 }, model.Frames.Select(row => row.Id));
        await Push(analyzer, ProtocolDirection.Tx, Start.AddMilliseconds(-7));
        model.Refresh();
        Assert.Equal(new long[] { 4, 3 }, model.Frames.Select(row => row.Id));
        var requestsBeforeAppend = latestRequests;
        await Push(analyzer, ProtocolDirection.Rx, Start.AddMilliseconds(21));
        model.Refresh();
        Assert.Equal(new long[] { 4, 5 }, model.Frames.Select(row => row.Id));
        Assert.True(latestRequests > requestsBeforeAppend);
        Assert.Equal(3, analyzer.Snapshot().EvictedFrames);
        Assert.True(model.IsFollowingLatest);
    }

    private static ProtocolViewModel CreateModel(ModbusRtuAnalyzer analyzer) =>
        new(analyzer) { IsVisible = true, SelectedProtocolIndex = 1 };

    private static async Task Push(ModbusRtuAnalyzer analyzer, ProtocolDirection direction, DateTimeOffset timestamp)
    {
        var target = analyzer.Snapshot().TotalFrames + 1;
        Assert.True(analyzer.TryEnqueue(direction == ProtocolDirection.Tx ? Request : ExceptionResponse, direction, 1, timestamp));
        await Wait(analyzer, target);
    }

    private static async Task Wait(ModbusRtuAnalyzer analyzer, long frames)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (analyzer.Snapshot().TotalFrames < frames) await Task.Delay(5, timeout.Token);
    }
}
