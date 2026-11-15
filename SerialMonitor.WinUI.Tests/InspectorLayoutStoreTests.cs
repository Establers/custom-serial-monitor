using System.Text.Json;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class InspectorLayoutStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "inspector-layout-" + Guid.NewGuid().ToString("N"));
    private string LayoutPath => Path.Combine(_directory, "layout.json");

    [Theory]
    [InlineData("Left", false)]
    [InlineData("Right", true)]
    [InlineData("Bottom", false)]
    public async Task NewStoreRestoresPositionBothSizesAndCollapse(string position, bool collapsed)
    {
        var expected = new InspectorLayoutState(Enum.Parse<InspectorDockPosition>(position), 187.5, 421.25, collapsed);
        await new InspectorLayoutStore(LayoutPath).SaveAsync(expected, CancellationToken.None);
        Assert.Equal(expected, await new InspectorLayoutStore(LayoutPath).LoadAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task MissingOrOlderFileUsesBottomDefaults()
    {
        var store = new InspectorLayoutStore(LayoutPath);
        Assert.Equal(new InspectorLayoutState(), await store.LoadAsync(CancellationToken.None));
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(LayoutPath, "{}");
        Assert.Equal(new InspectorLayoutState(), await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidNumericValuesCannotHideThePanelOrRequestEnormousLayout()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(LayoutPath, "{\"Position\":99,\"BottomHeight\":-5,\"SideWidth\":1e90}");
        Assert.Equal(new InspectorLayoutState(), await new InspectorLayoutStore(LayoutPath).LoadAsync(CancellationToken.None));
        var normalized = new InspectorLayoutState(BottomHeight: double.NaN, SideWidth: double.PositiveInfinity).Normalize();
        Assert.Null(normalized.BottomHeight);
        Assert.Null(normalized.SideWidth);
    }

    [Fact]
    public async Task CorruptFileReportsFailureWithoutOverwritingIt()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(LayoutPath, "broken json");
        await Assert.ThrowsAsync<JsonException>(() => new InspectorLayoutStore(LayoutPath).LoadAsync(CancellationToken.None));
        Assert.Equal("broken json", await File.ReadAllTextAsync(LayoutPath));
    }

    [Fact]
    public async Task CancelledSaveKeepsThePreviousCompleteFile()
    {
        var store = new InspectorLayoutStore(LayoutPath);
        var previous = new InspectorLayoutState(InspectorDockPosition.Right, 200, 430);
        await store.SaveAsync(previous, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(), cancelled.Token));
        Assert.Equal(previous, await store.LoadAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task IndependentWritersPublishOnlyCompleteSnapshots()
    {
        var states = Enumerable.Range(0, 12).Select(i => new InspectorLayoutState(
            InspectorDockPosition.Left, 180 + i, 400 + i, i % 2 == 0)).ToArray();
        await Task.WhenAll(states.Select(state => new InspectorLayoutStore(LayoutPath).SaveAsync(state, CancellationToken.None)));
        Assert.Contains(await new InspectorLayoutStore(LayoutPath).LoadAsync(CancellationToken.None), states);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
