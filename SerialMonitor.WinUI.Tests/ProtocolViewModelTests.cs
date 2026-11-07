using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.ViewModels;
using Microsoft.UI.Xaml;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProtocolViewModelTests
{
    private static readonly byte[] Request = Convert.FromHexString("010300000002C40B");

    [Fact]
    public async Task LeavingTab_PreservesBackgroundAnalysisAndSelection()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        var viewModel = new ProtocolViewModel(analyzer) { IsVisible = true, SelectedProtocolIndex = 1 };
        Assert.Equal("Protocol", viewModel.TabTitle);
        Assert.Equal(Visibility.Visible, viewModel.EmptyStateVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.DetailsVisibility);
        analyzer.TryEnqueue(Request, ProtocolDirection.Tx, 1, DateTimeOffset.Now);
        await Wait(analyzer, state => state.TotalFrames == 1);
        viewModel.Refresh();
        viewModel.SelectedFrame = Assert.Single(viewModel.Frames);
        var selectedId = viewModel.SelectedFrame.Id;
        Assert.Equal(Visibility.Collapsed, viewModel.EmptyStateVisibility);
        Assert.Equal(Visibility.Visible, viewModel.DetailsVisibility);
        Assert.Contains("Start address (base 0): 0", viewModel.Details);
        viewModel.IsVisible = false;
        Assert.Empty(viewModel.Frames);
        Assert.Equal(Visibility.Collapsed, viewModel.DetailsVisibility);
        Assert.True(analyzer.Enabled);
        analyzer.TryEnqueue(Request, ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        await Wait(analyzer, state => state.TotalFrames == 2);
        viewModel.IsVisible = true;
        Assert.Equal(2, viewModel.Frames.Count);
        Assert.Equal(selectedId, viewModel.SelectedFrame!.Id);
        Assert.Equal(Visibility.Visible, viewModel.DetailsVisibility);
    }

    [Fact]
    public async Task RetentionDoesNotLeaveSelectedOldPacketPinned_AndDisableKeepsRecentHistory()
    {
        await using var analyzer = new ModbusRtuAnalyzer(historyCapacity: 2);
        var viewModel = new ProtocolViewModel(analyzer) { IsVisible = true, SelectedProtocolIndex = 1 };
        analyzer.TryEnqueue(Request, ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        await Wait(analyzer, state => state.TotalFrames == 1);
        viewModel.Refresh();
        viewModel.SelectedFrame = Assert.Single(viewModel.Frames);
        analyzer.TryEnqueue(Request.Concat(Request).ToArray(), ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        await Wait(analyzer, state => state.TotalFrames == 3);
        viewModel.Refresh();
        Assert.Null(viewModel.SelectedFrame);
        Assert.Equal(Visibility.Visible, viewModel.SelectionHintVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.DetailsVisibility);
        Assert.Equal(2, viewModel.Frames.Count);
        viewModel.SelectedProtocolIndex = 0;
        Assert.False(analyzer.Enabled);
        Assert.Equal(2, viewModel.Frames.Count);
        viewModel.Clear();
        Assert.Empty(viewModel.Frames);
        Assert.Equal(Visibility.Visible, viewModel.EmptyStateVisibility);
        Assert.Equal(Visibility.Visible, viewModel.SelectionHintVisibility);
        Assert.Empty(analyzer.Snapshot().Frames!);
    }

    [Fact]
    public async Task TwoWindows_HaveIndependentAnalysisAndHistory()
    {
        await using var first = new ModbusRtuAnalyzer();
        await using var second = new ModbusRtuAnalyzer();
        var one = new ProtocolViewModel(first) { IsVisible = true, SelectedProtocolIndex = 1 };
        var two = new ProtocolViewModel(second) { IsVisible = true };
        first.TryEnqueue(Request, ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        Assert.False(second.TryEnqueue(Request, ProtocolDirection.Rx, 1, DateTimeOffset.Now));
        await Wait(first, state => state.TotalFrames == 1);
        one.Refresh(); two.Refresh();
        Assert.Single(one.Frames);
        Assert.Empty(two.Frames);
    }

    private static async Task Wait(ModbusRtuAnalyzer analyzer, Func<ProtocolAnalysisSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate(analyzer.Snapshot())) await Task.Delay(5, timeout.Token);
    }
}
