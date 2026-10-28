using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal static class ProfileSequenceMerge
{
    public static Dictionary<string, string> Snapshot(IEnumerable<CommandSequence> sequences) =>
        sequences.ToDictionary(sequence => sequence.Name, Fingerprint, StringComparer.OrdinalIgnoreCase);

    public static List<CommandSequence> Clone(IEnumerable<CommandSequence> sequences) => sequences.Select(sequence => new CommandSequence
    {
        Name = sequence.Name,
        RepeatCount = sequence.RepeatCount,
        Steps = new ObservableCollection<CommandSequenceStep>(sequence.Steps.Select(step => new CommandSequenceStep
        {
            Name = step.Name, IsEnabled = step.IsEnabled, CommandText = step.CommandText, LineEndingMode = step.LineEndingMode,
            DelayAfterMs = step.DelayAfterMs, Comment = step.Comment, TxMode = step.TxMode,
            CaptureMs = step.CaptureMs, Expect = step.Expect is { } expect ? expect with { } : null
        }))
    }).ToList();

    public static List<CommandSequence> Merge(IReadOnlyDictionary<string, string> baseline,
        IReadOnlyList<CommandSequence> local, IReadOnlyList<CommandSequence> disk)
    {
        var localByName = local.ToDictionary(sequence => sequence.Name, StringComparer.OrdinalIgnoreCase);
        var diskByName = disk.ToDictionary(sequence => sequence.Name, StringComparer.OrdinalIgnoreCase);
        var localHashes = Snapshot(local);
        var diskHashes = Snapshot(disk);
        var names = local.Select(sequence => sequence.Name).Concat(disk.Select(sequence => sequence.Name))
            .Concat(baseline.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var merged = new List<CommandSequence>();
        foreach (var name in names)
        {
            baseline.TryGetValue(name, out var before);
            localHashes.TryGetValue(name, out var here);
            diskHashes.TryGetValue(name, out var there);
            CommandSequence? selected;
            if (here == before) diskByName.TryGetValue(name, out selected);
            else if (there == before || here == there) localByName.TryGetValue(name, out selected);
            else throw new ProfileConflictException("Sequence '" + name + "' changed in another application instance. Reload the profile and review its definition before saving again; no profile changes were written.");
            if (selected is not null) merged.Add(selected);
        }
        return merged;
    }

    private static string Fingerprint(CommandSequence sequence)
    {
        // Only persistent fields participate. UI derived labels, row numbers and
        // JSON enum formatting cannot turn an unchanged definition into an edit.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sequence.Name, sequence.RepeatCount,
            Steps = sequence.Steps.Select(step => new
            {
                step.Name, step.IsEnabled, step.CommandText, step.LineEndingMode, step.DelayAfterMs,
                step.Comment, step.TxMode, step.CaptureMs, step.Expect
            })
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
