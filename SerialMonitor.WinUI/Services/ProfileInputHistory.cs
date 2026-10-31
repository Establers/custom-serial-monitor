using System.Globalization;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal static class ProfileInputHistory
{
    public const int MaxSearchHistoryCount = 50;

    public static string PortKey(string? port)
    {
        var key = (port ?? string.Empty).Trim().ToUpperInvariant();
        if (key.StartsWith(@"\\.\", StringComparison.Ordinal)) key = key[4..];
        if (key.StartsWith("COM", StringComparison.Ordinal) &&
            uint.TryParse(key.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
            return "COM" + number.ToString(CultureInfo.InvariantCulture);
        return key;
    }

    public static List<string> NormalizeSearchHistory(IEnumerable<string>? terms) =>
        (terms ?? []).Where(term => !string.IsNullOrWhiteSpace(term))
            .Reverse().Distinct(StringComparer.Ordinal).Take(MaxSearchHistoryCount).Reverse().ToList();

    public static InstanceInputHistory Snapshot(InstanceInputHistory history) => new()
    {
        CommandHistory = (history.CommandHistory ?? [])
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.CommandText))
            .OrderByDescending(entry => entry.LastSentTime)
            .DistinctBy(entry => entry.CommandText, StringComparer.Ordinal).Take(100)
            .Select(entry => new CommandHistoryEntry
            {
                // This is a TX payload; padding changes the transmitted bytes.
                CommandText = entry.CommandText,
                LastSentTime = entry.LastSentTime == default ? DateTimeOffset.Now : entry.LastSentTime
            }).ToList(),
        SearchHistory = NormalizeSearchHistory(history.SearchHistory),
        LastSearchText = history.LastSearchText ?? string.Empty
    };

    public static InstanceInputHistory Capture(AppProfile profile) => Snapshot(new InstanceInputHistory
    {
        CommandHistory = profile.CommandHistory,
        SearchHistory = profile.UiSettings?.SearchHistory ?? [],
        LastSearchText = profile.UiSettings?.LastSearchText ?? string.Empty
    });

    public static Dictionary<string, InstanceInputHistory> SnapshotPorts(Dictionary<string, InstanceInputHistory>? histories)
    {
        var result = new Dictionary<string, InstanceInputHistory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (port, history) in histories ?? [])
        {
            if (history is null) continue;
            var key = PortKey(port);
            var copy = Snapshot(history);
            result[key] = result.TryGetValue(key, out var existing) ? Combine(existing, copy) : copy;
        }
        return result;
    }

    public static void Prepare(AppProfile profile)
    {
        profile.PortHistories = SnapshotPorts(profile.PortHistories);
        if (profile.PortHistoryMigrationVersion >= 1) return;

        // Slot histories have no device identity. Import them once to the saved
        // port, and retain each original slot plus the original common history.
        var legacy = Capture(profile);
        profile.LegacyInputHistory ??= Snapshot(legacy);
        if (legacy.SearchHistory.Count == 0 && !string.IsNullOrWhiteSpace(legacy.LastSearchText))
            legacy.SearchHistory.Add(legacy.LastSearchText);
        var combined = Snapshot(legacy);
        foreach (var history in (profile.InstanceHistories ?? []).OrderBy(pair => pair.Key).Select(pair => pair.Value))
            if (history is not null) combined = Combine(combined, history);
        if (combined.CommandHistory.Count > 0 || combined.SearchHistory.Count > 0 || combined.LastSearchText.Length > 0)
        {
            var key = PortKey(profile.SerialSettings?.PortName);
            profile.PortHistories[key] = profile.PortHistories.TryGetValue(key, out var existing)
                ? Combine(combined, existing) : combined;
        }
        profile.PortHistoryMigrationVersion = 1;
    }

    public static void Apply(AppProfile profile)
    {
        Prepare(profile);
        var key = PortKey(profile.ActiveHistoryPort ?? profile.SerialSettings.PortName);
        var history = Snapshot(profile.PortHistories.GetValueOrDefault(key) ?? new());
        profile.ActiveHistoryPort = key;
        profile.CommandHistory = history.CommandHistory;
        profile.UiSettings.SearchHistory = history.SearchHistory;
        profile.UiSettings.LastSearchText = history.LastSearchText;
    }

    public static Dictionary<string, InstanceInputHistory> CapturePorts(AppProfile profile)
    {
        Prepare(profile);
        var result = SnapshotPorts(profile.PortHistories);
        result[PortKey(profile.ActiveHistoryPort ?? profile.SerialSettings.PortName)] = Capture(profile);
        return result;
    }

    // Merge changes relative to what this window observed, rather than replacing
    // a whole port with a stale snapshot. Unchanged windows cannot undo a clear.
    public static Dictionary<string, InstanceInputHistory> Merge(
        Dictionary<string, InstanceInputHistory>? baseline,
        Dictionary<string, InstanceInputHistory> local,
        Dictionary<string, InstanceInputHistory>? disk)
    {
        var result = SnapshotPorts(disk);
        foreach (var (port, history) in local)
        {
            var before = baseline?.GetValueOrDefault(port) ?? new();
            var current = result.GetValueOrDefault(port) ?? new();
            var submitted = Snapshot(history);
            var previous = Snapshot(before);
            var commands = current.CommandHistory.ToDictionary(entry => entry.CommandText, StringComparer.Ordinal);
            var previousCommands = previous.CommandHistory.ToDictionary(entry => entry.CommandText, StringComparer.Ordinal);
            var submittedCommands = submitted.CommandHistory.ToDictionary(entry => entry.CommandText, StringComparer.Ordinal);
            foreach (var (text, entry) in previousCommands)
                if (!submittedCommands.ContainsKey(text) && commands.TryGetValue(text, out var latest) && latest.LastSentTime == entry.LastSentTime)
                    commands.Remove(text);
            foreach (var (text, entry) in submittedCommands)
                if ((!previousCommands.TryGetValue(text, out var old) || old.LastSentTime != entry.LastSentTime) &&
                    (!commands.TryGetValue(text, out var latest) || latest.LastSentTime <= entry.LastSentTime))
                    commands[text] = entry;

            var terms = current.SearchHistory.Where(term => !previous.SearchHistory.Contains(term, StringComparer.Ordinal) ||
                submitted.SearchHistory.Contains(term, StringComparer.Ordinal)).ToList();
            var baselineIndex = 0;
            var touchedIndex = 0;
            // A prefix that remains in the old relative order was not searched
            // again. Only replay the appended/moved suffix onto the disk list.
            for (; touchedIndex < submitted.SearchHistory.Count; touchedIndex++)
            {
                var found = previous.SearchHistory.FindIndex(baselineIndex,
                    term => string.Equals(term, submitted.SearchHistory[touchedIndex], StringComparison.Ordinal));
                if (found < 0) break;
                baselineIndex = found + 1;
            }
            foreach (var term in submitted.SearchHistory.Skip(touchedIndex))
            {
                terms.RemoveAll(value => string.Equals(value, term, StringComparison.Ordinal));
                terms.Add(term);
            }
            result[port] = Snapshot(new InstanceInputHistory
            {
                CommandHistory = commands.Values.ToList(),
                SearchHistory = terms,
                LastSearchText = submitted.LastSearchText != previous.LastSearchText ? submitted.LastSearchText : current.LastSearchText
            });
        }
        return result;
    }

    private static InstanceInputHistory Combine(InstanceInputHistory first, InstanceInputHistory second) => Snapshot(new()
    {
        CommandHistory = (first.CommandHistory ?? []).Concat(second.CommandHistory ?? []).ToList(),
        SearchHistory = (first.SearchHistory ?? []).Concat(second.SearchHistory ?? []).ToList(),
        LastSearchText = !string.IsNullOrEmpty(second.LastSearchText) ? second.LastSearchText : first.LastSearchText
    });
}
