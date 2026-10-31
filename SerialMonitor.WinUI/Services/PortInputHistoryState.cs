using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

// Owned by the UI thread. Switching ports never performs disk I/O.
internal sealed class PortInputHistoryState
{
    private Dictionary<string, InstanceInputHistory> _histories = new(StringComparer.OrdinalIgnoreCase);
    public string ActivePort { get; private set; } = string.Empty;

    public void Load(AppProfile profile)
    {
        ProfileInputHistory.Prepare(profile);
        _histories = ProfileInputHistory.SnapshotPorts(profile.PortHistories);
        ActivePort = ProfileInputHistory.PortKey(profile.ActiveHistoryPort ?? profile.SerialSettings.PortName);
    }

    public void Capture(InstanceInputHistory history) => _histories[ActivePort] = ProfileInputHistory.Snapshot(history);

    public void RecordCommand(string port, string commandText, DateTimeOffset sentAt)
    {
        if (string.IsNullOrWhiteSpace(commandText)) return;
        var key = ProfileInputHistory.PortKey(port);
        var history = ProfileInputHistory.Snapshot(_histories.GetValueOrDefault(key) ?? new());
        history.CommandHistory.RemoveAll(entry => string.Equals(entry.CommandText, commandText, StringComparison.Ordinal));
        history.CommandHistory.Insert(0, new CommandHistoryEntry { CommandText = commandText, LastSentTime = sentAt });
        _histories[key] = ProfileInputHistory.Snapshot(history);
    }

    public InstanceInputHistory SwitchTo(string? port)
    {
        ActivePort = ProfileInputHistory.PortKey(port);
        return ProfileInputHistory.Snapshot(_histories.GetValueOrDefault(ActivePort) ?? new());
    }

    public Dictionary<string, InstanceInputHistory> Snapshot() => ProfileInputHistory.SnapshotPorts(_histories);
}
