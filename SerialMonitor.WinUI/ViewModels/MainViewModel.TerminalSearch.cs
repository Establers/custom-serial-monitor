using System.Text.Json;

namespace SerialMonitor.WinUI.ViewModels;

internal sealed record TerminalSearchContent(long SessionId, long SnapshotId, long Revision,
    IReadOnlyList<VisibleLogSearchLine> Lines)
{
    internal static TerminalSearchContent Parse(string json, long expectedSession)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(root.GetProperty("error").GetString());
        var session = root.GetProperty("sessionId").GetInt64();
        if (session != expectedSession) throw new InvalidOperationException("The terminal search session has ended.");
        var lines = root.GetProperty("lines").EnumerateArray().Select(line => new VisibleLogSearchLine(
            line.GetProperty("lineId").GetInt64(), line.GetProperty("text").GetString() ?? string.Empty, 0)).ToArray();
        return new(session, root.GetProperty("snapshotId").GetInt64(), root.GetProperty("revision").GetInt64(), lines);
    }
}

public sealed partial class MainViewModel
{
    private TerminalSearchContent? _terminalSearchContent;
    private long _terminalScreenRevision;
    internal Func<long, CancellationToken, Task<TerminalSearchContent>>? CaptureTerminalSearchContentAsync { get; set; }

    private void ResetTerminalScreenSearch()
    {
        _terminalSearchContent = null;
        _terminalScreenRevision = 0;
        ClearSearchResultsForVisibleLogReset();
    }

    public void TerminalScreenSearchChanged(long sessionId, long revision)
    {
        if (!IsTerminalDirectInputEnabled || sessionId != TerminalDirectInputSessionId || revision <= _terminalScreenRevision) return;
        _terminalScreenRevision = revision;
        if (_terminalSearchContent is null || revision > _terminalSearchContent.Revision) MarkSearchResultsStale();
    }
}
