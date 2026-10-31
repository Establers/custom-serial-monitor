namespace SerialMonitor.WinUI.Services;

// Runtime evidence is session-local, like the event list. An exclusive owner
// lease prevents cleanup of another running app's files.
internal sealed class EventArchiveRuntime
{
    internal static EventArchiveRuntime Current { get; } = new();
    private FileStream? _lease;
    private readonly Lazy<Task<string>> _directory;
    private Task? _cleanup;
    private EventArchiveRuntime() => _directory = new(() => Task.Run(Initialize));
    internal Task<string> GetDirectoryAsync(CancellationToken token) => _directory.Value.WaitAsync(token);
    private string Initialize()
    {
        var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SerialMonitor", "event-context"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"instance-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _lease = new FileStream(Path.Combine(path, ".owner"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        _cleanup = Task.Run(() =>
        {
            try
            {
                foreach (var old in Directory.EnumerateDirectories(root, "instance-*").Take(512))
                    if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) RuntimeArchiveCleanup.TryDelete(root, old);
            }
            catch (IOException ex) { System.Diagnostics.Trace.TraceError("Event cache cleanup: {0}", ex); }
            catch (UnauthorizedAccessException ex) { System.Diagnostics.Trace.TraceError("Event cache cleanup: {0}", ex); }
        });
        return path;
    }
}

internal static class RuntimeArchiveCleanup
{
    internal readonly record struct Entry(string Path, FileAttributes Attributes);
    internal static bool TryCollect(string root, string target, Func<string, IEnumerable<Entry>> enumerate, out List<Entry> entries)
    {
        entries = [];
        var parent = Path.GetFullPath(root);
        var full = Path.GetFullPath(target);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("instance-", StringComparison.Ordinal)) return false;
        var pending = new Stack<string>(); pending.Push(full);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in enumerate(directory))
            {
                var path = Path.GetFullPath(entry.Path);
                if (!path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                entries.Add(entry);
                if (entries.Count > 100_000 || pending.Count > 4096) return false;
                if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
        return true;
    }

    internal static void TryDelete(string root, string target)
    {
        try
        {
            var full = Path.GetFullPath(target);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
            var owner = Path.Combine(full, ".owner");
            if ((File.GetAttributes(owner) & FileAttributes.ReparsePoint) != 0) return;
            using (new FileStream(owner, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (!TryCollect(root, full, path => new DirectoryInfo(path).EnumerateFileSystemInfos()
                    .Select(item => new Entry(item.FullName, item.Attributes)), out var entries)) return;
                foreach (var entry in entries.Where(e => (e.Attributes & FileAttributes.Directory) == 0))
                {
                    if (string.Equals(entry.Path, owner, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsOrdinaryChild(full, entry.Path)) return;
                    File.Delete(entry.Path);
                }
                foreach (var entry in entries.Where(e => (e.Attributes & FileAttributes.Directory) != 0)
                    .OrderByDescending(e => e.Path.Length))
                {
                    if (!IsOrdinaryChild(full, entry.Path)) return;
                    Directory.Delete(entry.Path, recursive: false);
                }
            }
            File.Delete(owner);
            Directory.Delete(full, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static bool IsOrdinaryChild(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        for (var current = full; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0;
    }
}
