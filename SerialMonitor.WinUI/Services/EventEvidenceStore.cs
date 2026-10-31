using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

// Full context stays on disk. Frames are independently bounded transport
// records; selection and export never deserialize a whole context into memory.
public sealed class EventEvidenceStore : IEventEvidenceStore
{
    public const int PageCharacterLimit = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, WeakReference<EventEvidenceReference>> _references = [];
    private string? _directory;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sweeper;
    private bool _disposed;
    private readonly int _pageCharacterLimit;
    public event EventHandler<string>? Error;
    public EventEvidenceStore(string? directory = null, int pageCharacterLimit = PageCharacterLimit)
    {
        if (pageCharacterLimit is < 1024 or > PageCharacterLimit) throw new ArgumentOutOfRangeException(nameof(pageCharacterLimit));
        _pageCharacterLimit = pageCharacterLimit;
        _directory = directory;
        _sweeper = Task.Run(SweepPeriodicallyAsync);
    }

    public async Task<DetectedEvent> StoreEventAsync(DetectedEvent item, CancellationToken cancellationToken)
    {
        var reference = await WriteAsync(new(item, item.BeforeContextLines, item.AfterContextLines,
            item.BeforeContextLines.Count, 0, item.Timestamp), false, cancellationToken);
        // Keep the whole ordinary message and its styling. Only redundant
        // context payloads move out of the screen's object graph.
        return new(item.Timestamp, item.RuleName, item.Keyword, item.Direction, item.Message,
            item.SourceLogLine, trayNotificationEnabled: item.TrayNotificationEnabled,
            soundNotificationEnabled: item.SoundNotificationEnabled, popupNotificationEnabled: item.PopupNotificationEnabled,
            notificationCooldownSeconds: item.NotificationCooldownSeconds, showInEventList: item.ShowInEventList,
            triggerSequenceName: item.TriggerSequenceName, id: item.Id, buildMessageSegments: false,
            evidence: reference, messageSegments: item.MessageSegments, contextError: item.ContextError);
    }

    public Task<EventEvidenceReference> StoreContextAsync(DetectedEventContext context, CancellationToken cancellationToken) =>
        WriteAsync(context, true, cancellationToken);

    private async Task<EventEvidenceReference> WriteAsync(DetectedEventContext context, bool complete, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        EventEvidenceReference reference;
        string? temporary = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _directory ??= Path.Combine(await EventArchiveRuntime.Current.GetDirectoryAsync(token), "events-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            if (!_references.TryGetValue(context.EventId, out var weak) || !weak.TryGetTarget(out reference!))
            {
                reference = new(context.EventId, Path.Combine(_directory, context.EventId.ToString("N") + ".event"));
                _references[context.EventId] = new(reference);
            }
            // Context completion can overtake initial UI delivery.
            if (reference.IsComplete && !complete) return reference;
            temporary = reference.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var pins = new List<IArchivedLogLine>();
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await WriteFrameAsync(file, new Header(complete, context.BeforeContextLines.Count,
                    context.AfterContextLines.Count, context.BeforeContextLineLimit, context.AfterContextLineLimit, reference.Version + 1,
                    context.Error ?? context.Event.ContextError), token);
                await WriteContextAsync(file, context.BeforeContextLines, "BEFORE", pins, token);
                if (context.Event.ArchivedSource is { } archived)
                    await WriteReferenceAsync(file, archived, "MATCHED", pins, token);
                else
                {
                    var matched = context.Event.SourceLogLine ?? new LogLine(context.Event.Timestamp, context.Event.Direction, context.Event.Message);
                    await WriteFrameAsync(file, new Entry("MATCHED", ArchivedLogRecord.From(matched)), token);
                }
                await WriteContextAsync(file, context.AfterContextLines, "AFTER", pins, token);
            }
            File.Move(temporary, reference.Path, overwrite: true);
            reference.ContextPins = pins.ToArray();
            temporary = null;
            reference.BeforeCount = context.BeforeContextLines.Count;
            reference.AfterCount = context.AfterContextLines.Count;
            reference.IsComplete = complete;
            reference.Version++;
            reference.Error = null;
            reference.CaptureError = context.Error ?? context.Event.ContextError;
            Sweep();
            return reference;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Presentation storage failure must not end rule processing or RX.
            reference = new(context.EventId, string.Empty) { Error = ex.Message };
            Error?.Invoke(this, "Event context storage: " + ex.Message);
            return reference;
        }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            _gate.Release();
        }
    }

    public async Task<EventEvidencePage> ReadPageAsync(EventEvidenceReference reference, long offset,
        Func<LogLine, string> format, CancellationToken cancellationToken, int? expectedVersion = null)
    {
        // The caller uses Task.Run: JSON decoding and formatting belong to the
        // worker too, including each synchronous journal-backed line access.
        var opened = await OpenAsync(reference, cancellationToken);
        await using var file = opened.File;
        var header = await ReadFrameAsync<Header>(file, cancellationToken);
        if (expectedVersion is { } version && version != header.Version) offset = 0;
        var beginning = offset == 0 ? file.Position : offset;
        if (beginning < file.Position || beginning > file.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        file.Position = beginning;
        var text = new StringBuilder();
        text.AppendLine($"BEFORE ({header.BeforeCount}/{header.BeforeLimit}) / AFTER ({header.AfterCount}/{header.AfterLimit})");
        if (header.CaptureError is { } error) text.AppendLine("Context incomplete: " + error);
        string? section = null;
        while (file.Position < file.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = file.Position;
            var entry = await ReadFrameAsync<Entry>(file, cancellationToken);
            var rendered = format(await ReadEntryAsync(entry, opened.Pins, cancellationToken));
            var extra = rendered.Length + entry.Section.Length + 8;
            if (text.Length + extra > _pageCharacterLimit && text.Length > 256)
            { file.Position = position; break; }
            if (text.Length + extra > _pageCharacterLimit)
                throw new IOException("An individual event record exceeds the display page limit. Export the full context to read it.");
            if (section != entry.Section) { text.AppendLine(); text.AppendLine(entry.Section); section = entry.Section; }
            text.AppendLine(rendered);
        }
        return new(text.ToString(), offset, file.Position, file.Position < file.Length, header.Complete && header.CaptureError is null, header.Version);
    }

    public async Task ExportAsync(EventEvidenceReference reference, string destination, Func<LogLine, string> format,
        CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(destination);
        if (string.Equals(full, Path.GetFullPath(reference.Path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cannot overwrite the active event evidence file.");
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var opened = await OpenAsync(reference, cancellationToken);
            await using (var input = opened.File)
            await using (var output = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous), new UTF8Encoding(false)))
            {
                var header = await ReadFrameAsync<Header>(input, cancellationToken);
                await output.WriteLineAsync($"BEFORE ({header.BeforeCount}/{header.BeforeLimit}) / AFTER ({header.AfterCount}/{header.AfterLimit})".AsMemory(), cancellationToken);
                if (header.CaptureError is { } error) await output.WriteLineAsync(("Context incomplete: " + error).AsMemory(), cancellationToken);
                string? section = null;
                while (input.Position < input.Length)
                {
                    var entry = await ReadFrameAsync<Entry>(input, cancellationToken);
                    if (entry.Section != section)
                    { await output.WriteLineAsync(entry.Section.AsMemory(), cancellationToken); section = entry.Section; }
                    await output.WriteLineAsync(format(await ReadEntryAsync(entry, opened.Pins, cancellationToken)).AsMemory(), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<(FileStream File, Dictionary<ContextLineLocation, IArchivedLogLine> Pins)> OpenAsync(EventEvidenceReference reference, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (reference.Error is { } error) throw new IOException(error);
            var pins = new Dictionary<ContextLineLocation, IArchivedLogLine>();
            foreach (var pin in reference.ContextPins)
                if (pin is IStoredContextLine stored) pins.TryAdd(stored.Location, pin);
            return (new(reference.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous), pins);
        }
        finally { _gate.Release(); }
    }
    private static Task<LogLine> ReadEntryAsync(Entry entry, Dictionary<ContextLineLocation, IArchivedLogLine> pins, CancellationToken token)
    {
        if (entry.Location is { } location)
            return pins.TryGetValue(location, out var pin) ? pin.ReadAsync(token) :
                Task.FromException<LogLine>(new InvalidDataException("Event evidence reference is not owned by this context."));
        return Task.FromResult(entry.Line?.ToLine() ?? throw new InvalidDataException("Missing event record."));
    }
    private static async Task WriteContextAsync(Stream file, IReadOnlyList<LogLine> lines, string section, List<IArchivedLogLine> pins, CancellationToken token)
    {
        if (lines is ArchivedLogLines archived)
        {
            foreach (var reference in archived.References) await WriteReferenceAsync(file, reference, section, pins, token);
        }
        else await foreach (var line in ReadContextAsync(lines, token))
            await WriteFrameAsync(file, new Entry(section, ArchivedLogRecord.From(line)), token);
    }
    private static async Task WriteReferenceAsync(Stream file, IArchivedLogLine reference, string section, List<IArchivedLogLine> pins, CancellationToken token)
    {
        if (reference is IStoredContextLine stored && pins.Count < EventEvidenceReference.MaximumPins)
        {
            pins.Add(reference);
            await WriteFrameAsync(file, new Entry(section, null, stored.Location), token);
        }
        else await WriteFrameAsync(file, new Entry(section, ArchivedLogRecord.From(await reference.ReadAsync(token))), token);
    }
    private static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > ContextLineArchive.MaximumRecordBytes) throw new IOException("Event record exceeded 16 MiB storage frame limit.");
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await stream.WriteAsync(length, deadline.Token); await stream.WriteAsync(bytes, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Event evidence write exceeded its 5-second deadline."); }
    }
    private static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken token)
    {
        var length = new byte[4]; await stream.ReadExactlyAsync(length, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is < 1 or > ContextLineArchive.MaximumRecordBytes) throw new InvalidDataException("Invalid event evidence frame size.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Invalid event evidence frame.");
    }
    private static async IAsyncEnumerable<LogLine> ReadContextAsync(IReadOnlyList<LogLine> lines,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (lines is ArchivedLogLines archived)
        { await foreach (var line in archived.ReadAllAsync(token)) yield return line; }
        else
        { foreach (var line in lines) { token.ThrowIfCancellationRequested(); yield return line; } }
    }
    private void Sweep()
    {
        foreach (var (id, weak) in _references.ToArray())
        {
            if (weak.TryGetTarget(out _)) continue;
            try { File.Delete(Path.Combine(_directory!, id.ToString("N") + ".event")); _references.Remove(id); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private async Task SweepPeriodicallyAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await _gate.WaitAsync(_stop.Token);
                try { if (_directory is not null) Sweep(); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Error?.Invoke(this, "Event context cleanup: " + ex.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await _sweeper;
        await _gate.WaitAsync();
        try { _disposed = true; if (_directory is not null) Sweep(); }
        finally { _gate.Release(); }
        _stop.Dispose();
    }
    private sealed record Header(bool Complete, int BeforeCount, int AfterCount, int BeforeLimit, int AfterLimit, int Version, string? CaptureError);
    private sealed record Entry(string Section, ArchivedLogRecord? Line, ContextLineLocation? Location = null);
}
