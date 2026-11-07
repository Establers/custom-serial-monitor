using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProtocolBrowsingTests
{
    [Fact]
    public async Task FrozenList_SurvivesTabSwitchAndLiveEviction_WithoutRequestingNewFrameArrays()
    {
        await using var analyzer = new RecordingAnalyzer(historyCapacity: 2);
        var model = CreateModel(analyzer);
        await Push(analyzer, Request(1)); model.Refresh();
        model.SelectedFrame = Assert.Single(model.Frames);
        var frozenId = model.SelectedFrame.Id;
        model.IsDisplayPaused = true;
        var frameRequests = analyzer.FrameSnapshotRequests;
        for (byte unit = 2; unit <= 4; unit++) { await Push(analyzer, Request(unit)); model.Refresh(); }
        Assert.True(analyzer.Enabled);
        Assert.Equal(3, model.PendingDisplayFrames);
        Assert.Equal(frozenId, Assert.Single(model.Frames).Id);
        Assert.Equal(frameRequests, analyzer.FrameSnapshotRequests);
        Assert.Equal(2, analyzer.Snapshot(includeFrames: false).RetainedCount);
        model.IsVisible = false;
        Assert.Empty(model.Frames);
        model.IsVisible = true;
        Assert.Equal(frozenId, Assert.Single(model.Frames).Id);
        Assert.Equal(frozenId, model.SelectedFrame!.Id);
        Assert.Equal(frameRequests, analyzer.FrameSnapshotRequests);
        model.IsDisplayPaused = false;
        Assert.Equal(0, model.PendingDisplayFrames);
        Assert.Equal(2, model.Frames.Count);
        Assert.Null(model.SelectedFrame);
        Assert.All(model.Frames, row => Assert.True(row.Id > frozenId));
    }

    [Fact]
    public async Task PauseBeforeFirstPacket_AndClearWhilePaused_KeepBackgroundAnalysisRunning()
    {
        await using var analyzer = new RecordingAnalyzer();
        var model = CreateModel(analyzer);
        model.IsDisplayPaused = true;
        await Push(analyzer, Request(1)); model.Refresh();
        Assert.Empty(model.Frames);
        Assert.Equal(1, model.PendingDisplayFrames);
        model.ResetStatistics();
        Assert.Equal(1, model.PendingDisplayFrames);
        await Push(analyzer, Request(2)); model.Refresh();
        Assert.Equal(2, model.PendingDisplayFrames);
        model.Clear();
        Assert.True(model.IsDisplayPaused);
        Assert.Empty(model.Frames);
        Assert.Equal(0, model.PendingDisplayFrames);
        Assert.Equal(1, analyzer.Snapshot(includeFrames: false).Statistics.Tx);
        await Push(analyzer, Request(3)); model.Refresh();
        Assert.Empty(model.Frames);
        Assert.Equal(1, model.PendingDisplayFrames);
        model.IsDisplayPaused = false;
        Assert.Equal((byte)3, Assert.Single(model.Frames).Frame.Address);
        Assert.Equal(2, analyzer.Snapshot(includeFrames: false).Statistics.Tx);
    }

    [Fact]
    public async Task FiltersCombineUnitFunctionDirectionAndState_WithoutChangingStatistics()
    {
        await using var analyzer = new RecordingAnalyzer();
        var model = CreateModel(analyzer);
        await Push(analyzer, Request(1));
        await Push(analyzer, Frame(1, 0x83, 2), ProtocolDirection.Rx);
        await Push(analyzer, Request(2));
        await Push(analyzer, Request(1, 4));
        var invalid = Request(1); invalid[^1] ^= 1;
        await Push(analyzer, invalid);
        model.Refresh();
        var before = analyzer.Snapshot(includeFrames: false).Statistics;
        model.BeginFilterEdit();
        model.FilterUnitText = "1";
        model.FilterFunctionText = "0x03";
        Assert.True(model.TryApplyFilters());
        Assert.Equal(3, model.Frames.Count); // Request, exception 83, bad CRC request.
        Assert.All(model.Frames, row => Assert.Equal((byte)1, row.Frame.Address));
        model.FilterDirectionIndex = 1;
        Assert.True(model.TryApplyFilters());
        Assert.Equal(ProtocolFrameStatus.Exception, Assert.Single(model.Frames).Frame.Status);
        model.FilterDirectionIndex = 0;
        model.FilterStatusIndex = 1;
        Assert.True(model.TryApplyFilters());
        Assert.Equal(2, model.Frames.Count);
        Assert.All(model.Frames, row => Assert.True(row.IsError));
        model.FilterFunctionText = "83";
        Assert.True(model.TryApplyFilters());
        Assert.Equal((byte)0x83, Assert.Single(model.Frames).Frame.Code);
        Assert.Equal(before, analyzer.Snapshot(includeFrames: false).Statistics);
        model.ResetFilters();
        Assert.False(model.HasActiveFilters);
        Assert.Equal(5, model.Frames.Count);
    }

    [Fact]
    public async Task InvalidFilterInput_DoesNotReplaceAppliedConditions_AndUnappliedDraftIsCancelled()
    {
        await using var analyzer = new RecordingAnalyzer();
        var model = CreateModel(analyzer);
        await Push(analyzer, Request(1)); await Push(analyzer, Request(2)); model.Refresh();
        model.FilterUnitText = "1";
        Assert.True(model.TryApplyFilters());
        model.FilterUnitText = "256";
        Assert.False(model.TryApplyFilters());
        Assert.Equal(Visibility.Visible, model.FilterValidationVisibility);
        Assert.Equal((byte)1, Assert.Single(model.Frames).Frame.Address);
        model.BeginFilterEdit();
        Assert.Equal("1", model.FilterUnitText);
        Assert.Empty(model.FilterValidationError);
        model.FilterUnitText = "2";
        model.FilterFunctionText = "GG";
        Assert.False(model.TryApplyFilters());
        Assert.Equal((byte)1, Assert.Single(model.Frames).Frame.Address);
        model.BeginFilterEdit();
        Assert.Equal("1", model.FilterUnitText);
        Assert.Empty(model.FilterFunctionText);
        model.FilterUnitText = "2"; model.FilterFunctionText = "03";
        Assert.True(model.TryApplyFilters());
        Assert.Equal((byte)2, Assert.Single(model.Frames).Frame.Address);
    }

    [Fact]
    public async Task FilteringFrozenResults_DoesNotBringInNewTrafficUntilResume()
    {
        await using var analyzer = new RecordingAnalyzer();
        var model = CreateModel(analyzer);
        await Push(analyzer, Request(1)); await Push(analyzer, Request(2)); model.Refresh();
        model.IsDisplayPaused = true;
        await Push(analyzer, Request(3)); model.Refresh();
        model.FilterUnitText = "1"; Assert.True(model.TryApplyFilters());
        model.SelectedFrame = Assert.Single(model.Frames);
        model.FilterUnitText = "3"; Assert.True(model.TryApplyFilters());
        Assert.Empty(model.Frames);
        Assert.Null(model.SelectedFrame);
        Assert.Equal(Visibility.Visible, model.SelectionHintVisibility);
        Assert.Equal(1, model.PendingDisplayFrames);
        model.IsVisible = false; model.IsVisible = true;
        Assert.Empty(model.Frames);
        model.IsDisplayPaused = false;
        Assert.Equal((byte)3, Assert.Single(model.Frames).Frame.Address);
        model.ResetFilters();
        Assert.Equal(3, model.Frames.Count);
    }

    [Fact]
    public async Task FollowingStopsForSelection_PauseDoesNotScroll_AndResumeRequestsLatest()
    {
        await using var analyzer = new RecordingAnalyzer();
        var model = CreateModel(analyzer);
        var requests = 0;
        model.LatestRequested += (_, _) => requests++;
        Assert.True(model.IsFollowingLatest);
        await Push(analyzer, Request(1)); model.Refresh();
        Assert.True(requests > 0);
        model.SelectedFrame = Assert.Single(model.Frames);
        Assert.False(model.IsFollowingLatest);
        var stoppedAt = requests;
        await Push(analyzer, Request(2)); model.Refresh();
        Assert.Equal(stoppedAt, requests);
        model.IsFollowingLatest = true;
        Assert.True(requests > stoppedAt);
        model.IsDisplayPaused = true;
        var pausedAt = requests;
        await Push(analyzer, Request(3)); model.Refresh();
        Assert.Equal(pausedAt, requests);
        model.IsDisplayPaused = false;
        Assert.True(requests > pausedAt);
        Assert.Equal(3, model.Frames.Count);
    }

    [Fact]
    public async Task StateFilter_DistinguishesCrcErrorsIncompleteUnknownAndException()
    {
        await using var analyzer = new RecordingAnalyzer();
        var model = CreateModel(analyzer);
        var bad = Request(1); bad[^1] ^= 1;
        await Push(analyzer, bad);
        await Push(analyzer, new byte[] { 1, 3, 0 }, ProtocolDirection.Rx);
        await Push(analyzer, Frame(1, 0x41, 0x12, 0x34), ProtocolDirection.Rx);
        await Push(analyzer, Frame(1, 0x83, 2), ProtocolDirection.Rx);
        model.Refresh();
        Assert.Equal(4, model.Frames.Count);
        var crcError = Assert.Single(model.Frames.Where(row => row.Frame.Status == ProtocolFrameStatus.CrcError));
        var exception = Assert.Single(model.Frames.Where(row => row.Frame.Status == ProtocolFrameStatus.Exception));
        Assert.Equal(Visibility.Visible, crcError.ErrorVisibility);
        Assert.Equal(Visibility.Visible, exception.ErrorVisibility);
        Assert.Contains("02", exception.StatusText);
        Assert.NotEqual(crcError.StatusText, model.Frames.Single(row => row.Frame.Status == ProtocolFrameStatus.Incomplete).StatusText);
        for (var index = 2; index <= 5; index++)
        {
            model.FilterStatusIndex = index;
            Assert.True(model.TryApplyFilters());
            Assert.Single(model.Frames);
        }
        model.FilterStatusIndex = 6;
        Assert.True(model.TryApplyFilters());
        Assert.Equal(2, model.Frames.Count); // CRC-valid unknown function and exception.
        var stats = analyzer.Snapshot(includeFrames: false).Statistics;
        Assert.Equal(1, stats.CrcErrors);
        Assert.Equal(1, stats.Incomplete);
        Assert.Equal(1, stats.Exceptions);
        Assert.Equal(2, stats.CrcValid);
    }

    private static ProtocolViewModel CreateModel(IProtocolAnalyzer analyzer) => new(analyzer) { IsVisible = true, SelectedProtocolIndex = 1 };
    private static byte[] Request(byte unit, byte function = 3) => Frame(unit, function, 0, 0, 0, 2);
    private static byte[] Frame(params byte[] body)
    {
        var crc = ModbusRtuCrc.Compute(body);
        return body.Concat(new[] { (byte)crc, (byte)(crc >> 8) }).ToArray();
    }
    private static async Task Push(IProtocolAnalyzer analyzer, byte[] bytes, ProtocolDirection direction = ProtocolDirection.Tx)
    {
        var prior = analyzer.Snapshot(includeFrames: false).LastFrameId;
        Assert.True(analyzer.TryEnqueue(bytes, direction, 1, DateTimeOffset.Now));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (analyzer.Snapshot(includeFrames: false).LastFrameId <= prior) await Task.Delay(5, timeout.Token);
    }

    private sealed class RecordingAnalyzer(int historyCapacity = 100) : IProtocolAnalyzer
    {
        private readonly ModbusRtuAnalyzer _inner = new(historyCapacity: historyCapacity);
        public int FrameSnapshotRequests { get; private set; }
        public event EventHandler<string>? Error { add => _inner.Error += value; remove => _inner.Error -= value; }
        public bool Enabled => _inner.Enabled;
        public void SetEnabled(bool enabled) => _inner.SetEnabled(enabled);
        public bool TryEnqueue(ReadOnlyMemory<byte> bytes, ProtocolDirection direction, long session, DateTimeOffset timestamp, ProtocolInputTiming timing = default)
            => _inner.TryEnqueue(bytes, direction, session, timestamp, timing);
        public ProtocolAnalysisSnapshot Snapshot(long knownVersion = -1, bool includeFrames = true)
        {
            if (includeFrames) FrameSnapshotRequests++;
            return _inner.Snapshot(knownVersion, includeFrames);
        }
        public void Clear() => _inner.Clear();
        public void ResetStatistics() => _inner.ResetStatistics();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
