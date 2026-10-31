using System.Buffers.Binary;
using System.Collections;
using System.Text.Json;
using System.Collections.Concurrent;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal sealed record ContextLineLocation(string Path, long Position, int Length);
internal interface IStoredContextLine : IArchivedLogLine { ContextLineLocation Location { get; } }

internal sealed record ArchivedLogRecord(DateTimeOffset Timestamp, LogDirection Direction, string Text,
    byte[]? RawBytes, long? SequenceNumber, bool IsPartialRxSegment, bool IsPartialRxTerminator,
    string? DisplayText, LogRuleMatchMode ContentMode, string? FileText)
{
    internal static ArchivedLogRecord From(LogLine line) => new(line.Timestamp, line.Direction, line.Text,
        line.RawBytes, line.SequenceNumber, line.IsPartialRxSegment, line.IsPartialRxTerminator,
        ReferenceEquals(line.DisplayText, line.Text) ? null : line.DisplayText, line.ContentMode,
        ReferenceEquals(line.FileText, line.DisplayText) ? null : line.FileText);
    internal LogLine ToLine() => new(Timestamp, Direction, Text, RawBytes, SequenceNumber,
        IsPartialRxSegment, IsPartialRxTerminator, DisplayText, ContentMode, FileText);
}

// Detector-worker-owned, segmented journal. Segments hold weak references to
// their handles, so only before/after windows and pending consumers pin files.
internal sealed class ContextLineArchive : IAsyncDisposable
{
    internal const int MaximumRecordBytes = 16 * 1024 * 1024;
    private string? _directory;
    private static readonly ConcurrentDictionary<string, Segment> Segments = new();
    private static readonly CancellationTokenSource SweepStop = new();
    private static readonly Task SweepTask = StartSweeper();
    private FileStream? _writer;
    private Segment? _current;
    private int _records;
    internal ContextLineArchive(string? directory = null) => _directory = directory;

    internal async Task<IArchivedLogLine> AppendAsync(LogLine line, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ArchivedLogRecord.From(line));
        if (bytes.Length > MaximumRecordBytes) throw new IOException("Event context record exceeded 16 MiB.");
        if (_writer is null || _writer.Position + bytes.Length > 16 * 1024 * 1024 || _records >= 2048)
        {
            if (_writer is not null) { await _writer.DisposeAsync(); _current!.Closed = true; }
            _directory ??= Path.Combine(await EventArchiveRuntime.Current.GetDirectoryAsync(token), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _current = new Segment(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".lines"));
            Segments[_current.Path] = _current;
            _writer = new FileStream(_current.Path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous);
            _records = 0;
            Sweep();
        }
        var position = _writer.Position;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await _writer.WriteAsync(bytes, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Event context journal write exceeded its 5-second deadline."); }
        var reference = new LineReference(_current!.Path, position, bytes.Length);
        _current.References.Add(new(reference));
        _records++;
        return reference;
    }

    private static void Sweep()
    {
        foreach (var (path, segment) in Segments)
        {
            if (!segment.Closed || segment.References.Any(r => r.TryGetTarget(out _))) continue;
            try { File.Delete(path); Segments.TryRemove(path, out _); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) await _writer.DisposeAsync();
        if (_current is not null) _current.Closed = true;
        _writer = null; _current = null;
        Sweep();
    }
    private sealed record Segment(string Path)
    {
        internal volatile bool Closed;
        internal List<WeakReference<IArchivedLogLine>> References { get; } = [];
    }
    private static Task StartSweeper()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SweepStop.Cancel();
        return Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try { while (await timer.WaitForNextTickAsync(SweepStop.Token)) Sweep(); }
            catch (OperationCanceledException) when (SweepStop.IsCancellationRequested) { }
        });
    }
    private sealed record LineReference(string Path, long Position, int Length) : IStoredContextLine
    {
        public ContextLineLocation Location => new(Path, Position, Length);
        public LogLine Read()
        {
            using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Position = Position;
            var bytes = new byte[Length]; file.ReadExactly(bytes);
            return JsonSerializer.Deserialize<ArchivedLogRecord>(bytes)!.ToLine();
        }
        public async Task<LogLine> ReadAsync(CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    65536, FileOptions.Asynchronous);
                file.Position = Position;
                var bytes = new byte[Length]; await file.ReadExactlyAsync(bytes, deadline.Token);
                cancellationToken.ThrowIfCancellationRequested();
                return JsonSerializer.Deserialize<ArchivedLogRecord>(bytes)!.ToLine();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException("Event context journal read exceeded its 5-second deadline."); }
        }
    }
}

internal sealed class ArchivedLogLines(IReadOnlyList<IArchivedLogLine> references) : IReadOnlyList<LogLine>
{
    internal IReadOnlyList<IArchivedLogLine> References => references;
    public int Count => references.Count;
    public LogLine this[int index] => references[index].Read();
    public IEnumerator<LogLine> GetEnumerator()
    { foreach (var reference in references) yield return reference.Read(); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal async IAsyncEnumerable<LogLine> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    { foreach (var reference in references) yield return await reference.ReadAsync(token); }
}
