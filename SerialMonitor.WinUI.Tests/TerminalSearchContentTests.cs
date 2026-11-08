using System.Text.Json;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TerminalSearchContentTests
{
    [Fact]
    public void ScreenSnapshotUsesRenderedTextAndFencesTheOriginalSession()
    {
        const string json = """{"ok":true,"sessionId":7,"snapshotId":8,"revision":9,"lines":[{"lineId":1,"text":"device> STOP"},{"lineId":2,"text":"OK STOP"}]}""";
        var screen = TerminalSearchContent.Parse(json, 7);
        var result = VisibleLogSearchEngine.Build(screen.Lines, "STOP", new VisibleLogSearchOptions());
        Assert.Equal(2, result.TotalMatchCount);
        Assert.Equal(8, screen.SnapshotId);
        Assert.Equal(9, screen.Revision);
        Assert.Throws<InvalidOperationException>(() => TerminalSearchContent.Parse(json, 6));
        Assert.Throws<InvalidOperationException>(() => TerminalSearchContent.Parse("""{"ok":false,"error":"rendering"}""", 7));
    }

    // Browser export -> actual host matcher -> requests replayed by the browser.
    // This checks the count and selected cells share exactly the same source.
    [Fact]
    public void BrowserSnapshotsUseTheHostMatcherAndExportExactHighlightRequests()
    {
        var path = Environment.GetEnvironmentVariable("SERIALMONITOR_TERMINAL_SEARCH_FIXTURE");
        if (string.IsNullOrEmpty(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var requests = new List<object>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var content = TerminalSearchContent.Parse(item.GetProperty("snapshot").GetRawText(), item.GetProperty("sessionId").GetInt64());
            var term = item.GetProperty("term").GetString()!;
            var options = new VisibleLogSearchOptions(item.GetProperty("matchCase").GetBoolean(),
                item.GetProperty("wholeWord").GetBoolean(), item.GetProperty("regex").GetBoolean());
            var result = VisibleLogSearchEngine.Build(content.Lines, term, options);
            Assert.Equal(item.GetProperty("expectedCount").GetInt64(), result.TotalMatchCount);
            var targets = new List<object>();
            if (result.TryGetFirst(out var position))
            {
                for (long i = 0; i < result.TotalMatchCount; i++)
                {
                    var line = result.GetLine(position);
                    targets.Add(new { text = term, terminalSessionId = content.SessionId,
                        terminalSnapshotId = content.SnapshotId, targetLineId = line.LineId.ToString(),
                        targetPayloadOffset = position.PayloadOffset, matchLength = position.MatchLength,
                        expectedText = line.FullText.Substring(position.PayloadOffset, position.MatchLength) });
                    Assert.True(result.TryGetNext(position, out position));
                }
            }
            requests.Add(new { name = item.GetProperty("name").GetString(), targets });
        }
        File.WriteAllText(path + ".targets.json", JsonSerializer.Serialize(requests));
    }
}
