using System.Text;
using System.Text.Json;

namespace SerialMonitor.Core.Automation;

public sealed record AutomationRequest(string Operation, JsonElement Arguments);
public sealed record AutomationError(string Code, string Message);
public sealed record AutomationResponse(bool Success, JsonElement Data, AutomationError? Error = null)
{
    public static AutomationResponse Ok(object data) => new(true, JsonSerializer.SerializeToElement(data, AutomationJson.Options));
    // A failed capture can still contain RX and an uncertain or completed TX.
    // Keep that evidence alongside the error instead of discarding its payload.
    public static AutomationResponse Fail(string code, string message, object? data = null) =>
        new(false, JsonSerializer.SerializeToElement(data, AutomationJson.Options), new(code, message));
}

public sealed class AutomationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class AutomationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string? String(this JsonElement args, string name, string? fallback = null) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value.GetString() : fallback;

    public static int Integer(this JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value.GetInt32() : fallback;
}

// Both pipe RPC and MCP use bounded UTF-8 JSON lines. Keep unread characters
// between calls so multiple messages received in one OS read are not lost.
public sealed class JsonLineTooLargeException() : IOException("JSON message exceeds the size limit.");

public sealed class BoundedLineReader(TextReader reader, int maxCharacters = 128 * 1024)
{
    private readonly char[] _buffer = new char[4096];
    private int _position;
    private int _count;

    public Task<string?> ReadAsync(CancellationToken cancellationToken) => ReadCoreAsync(false, cancellationToken);

    // MCP is persistent: discard one oversized line and resume at its newline.
    // Bound both drain time and volume so a peer cannot tie up the server forever.
    public Task<string?> ReadRecoverableAsync(CancellationToken cancellationToken) => ReadCoreAsync(true, cancellationToken);

    private async Task<string?> ReadCoreAsync(bool recoverOversized, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var oversized = false;
        long consumed = 0;
        while (true)
        {
            if (_position == _count)
            {
                var read = reader.ReadAsync(_buffer.AsMemory(), drain.Token).AsTask();
                try { _count = await read.WaitAsync(drain.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    ObserveRead(read);
                    throw new IOException("Oversized JSON line did not terminate in time.");
                }
                catch
                {
                    ObserveRead(read);
                    throw;
                }
                _position = 0;
                if (_count == 0)
                    return !oversized && line.Length == 0 ? null : throw new IOException("Incomplete JSON line.");
            }
            var end = Array.IndexOf(_buffer, '\n', _position, _count - _position);
            var length = (end < 0 ? _count : end) - _position;
            consumed += length;
            if (!oversized && consumed > maxCharacters)
            {
                if (!recoverOversized) throw new JsonLineTooLargeException();
                oversized = true;
                line.Clear();
                drain.CancelAfter(TimeSpan.FromSeconds(5));
            }
            if (oversized && consumed > 4 * 1024 * 1024) throw new IOException("JSON line exceeds the recovery limit.");
            if (!oversized) line.Append(_buffer, _position, length);
            _position += length;
            if (end >= 0)
            {
                _position++;
                if (oversized) throw new JsonLineTooLargeException();
                return line.ToString().TrimEnd('\r');
            }
        }
    }

    private static void ObserveRead(Task read) => _ = read.ContinueWith(task => _ = task.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
