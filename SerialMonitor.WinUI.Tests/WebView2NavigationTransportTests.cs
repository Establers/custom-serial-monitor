using System.Runtime.CompilerServices;
using System.Text.Json;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class WebView2NavigationTransportTests
{
    [Fact]
    public async Task ActualWebViewSnapshotPreservesIntegralExponentTimestamps()
    {
        // Captured from CoreWebView2.ExecuteScriptAsync in runtime156.0.4314.50.
        // JavaScript JSON.stringify serializes this value differently, so an
        // Edge-only test would miss the native transport representation.
        var raw = Fixture("webview2-navigation-snapshot.json");
        using var parsed = JsonDocument.Parse(raw);
        var end = parsed.RootElement.GetProperty("endTime");
        Assert.Equal("1.793149200123e+12", end.GetRawText());
        Assert.False(end.TryGetInt64(out _));
        var bridge = new XtermLogNavigationBridge((_, _) => Task.FromResult(raw));

        var snapshot = await bridge.GetSnapshotAsync(0);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(1793149200000, snapshot.StartTime);
        Assert.Equal(1793149200123, snapshot.EndTime);
        Assert.Equal(1793149200000, snapshot.ViewportStartTime);
        Assert.Equal(1793149200123, snapshot.ViewportEndTime);
        Assert.Empty(snapshot.Bins);
    }

    [Fact]
    public async Task ActualWebViewPreviewUsesTheSameTimestampConversionAsSnapshot()
    {
        var raw = Fixture("webview2-navigation-preview.json");
        using var parsed = JsonDocument.Parse(raw);
        Assert.False(parsed.RootElement.GetProperty("timestamp").TryGetInt64(out _));
        var bridge = new XtermLogNavigationBridge((_, _) => Task.FromResult(raw));

        var preview = await bridge.ResolveAsync("#2");

        Assert.NotNull(preview);
        Assert.True(preview.Ok);
        Assert.Equal("2", preview.Id);
        Assert.Equal(1, preview.Index);
        Assert.Equal(1793149200123, preview.Timestamp);
    }

    private static string Fixture(string name, [CallerFilePath] string sourceFile = "")
    {
        // Fixtures remain exact capture bytes in the checkout; no browser or
        // native runtime is required when running this regression in CI.
        var root = Directory.GetParent(Path.GetDirectoryName(sourceFile)!)!.FullName;
        return File.ReadAllText(Path.Combine(root, "scripts", "testdata", name));
    }
}
