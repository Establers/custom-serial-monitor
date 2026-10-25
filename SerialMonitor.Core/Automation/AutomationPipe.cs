using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SerialMonitor.Core.Automation;

public sealed record AutomationInstance(string InstanceId, string PipeName, int ProcessId, DateTimeOffset StartedAt);

public static class AutomationDiscovery
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SerialMonitor", "automation", "instances");

    public static IReadOnlyList<AutomationInstance> List(string? directory = null)
    {
        var result = new List<AutomationInstance>();
        if (!Directory.Exists(directory ?? DirectoryPath)) return result;
        foreach (var path in Directory.EnumerateFiles(directory ?? DirectoryPath, "*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > 4096) continue;
                var instance = JsonSerializer.Deserialize<AutomationInstance>(File.ReadAllText(path), AutomationJson.Options);
                if (instance is null || string.IsNullOrEmpty(instance.InstanceId) || instance.PipeName is null ||
                    instance.PipeName.Length > 180 || !instance.PipeName.StartsWith("serialmonitor-instance-", StringComparison.Ordinal) ||
                    instance.PipeName != "serialmonitor-" + instance.InstanceId ||
                    !instance.PipeName.All(c => char.IsLetterOrDigit(c) || c == '-')) continue;
                using var process = Process.GetProcessById(instance.ProcessId);
                if (!process.HasExited && Math.Abs((new DateTimeOffset(process.StartTime.ToUniversalTime()) - instance.StartedAt).TotalSeconds) < 1)
                    result.Add(instance);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return result;
    }
}

public sealed class AutomationPipeHost : IAsyncDisposable
{
    private readonly Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(8);
    private readonly List<Task> _clients = [];
    private readonly string _descriptorPath;
    private readonly Action<Exception> _onError;
    private Task? _loop;

    public AutomationPipeHost(string instanceId, Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> handle,
        Action<Exception> onError, string? directory = null)
    {
        _handle = handle;
        _onError = onError;
        using var process = Process.GetCurrentProcess();
        Instance = new(instanceId, "serialmonitor-" + instanceId, Environment.ProcessId,
            new DateTimeOffset(process.StartTime.ToUniversalTime()));
        var root = directory ?? AutomationDiscovery.DirectoryPath;
        Directory.CreateDirectory(root);
        _descriptorPath = Path.Combine(root, instanceId + ".json");
    }

    public AutomationInstance Instance { get; }

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("Host already started.");
        _loop = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        try
        {
            var published = false;
            while (!_stop.IsCancellationRequested)
            {
                await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new(Instance.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    if (!published)
                    {
                        await File.WriteAllTextAsync(_descriptorPath, JsonSerializer.Serialize(Instance, AutomationJson.Options), _stop.Token).ConfigureAwait(false);
                        published = true;
                    }
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    var connected = pipe;
                    pipe = null;
                    lock (_clients)
                    {
                        _clients.RemoveAll(task => task.IsCompleted);
                        _clients.Add(ServeAsync(connected));
                    }
                }
                finally
                {
                    if (pipe is not null) { pipe.Dispose(); _slots.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _onError(ex); }
        finally
        {
            try { File.Delete(_descriptorPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _onError(ex); }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
        {
            try
            {
                lifetime.CancelAfter(TimeSpan.FromSeconds(90));
                var lines = new BoundedLineReader(reader);
                var line = await lines.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (line is null) return;
                var request = JsonSerializer.Deserialize<AutomationRequest>(line, AutomationJson.Options)
                    ?? throw new JsonException("Missing request.");
                // EOF while work is running means the calling CLI/MCP disappeared.
                // Cancel its capture and release the GUI TX reservation promptly.
                var disconnected = lines.ReadAsync(lifetime.Token);
                var work = _handle(request, lifetime.Token);
                if (await Task.WhenAny(work, disconnected).ConfigureAwait(false) == disconnected)
                {
                    lifetime.Cancel();
                    try { await work.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    return;
                }
                var response = await work.ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, AutomationJson.Options).AsMemory(), lifetime.Token).ConfigureAwait(false);
                lifetime.Cancel();
                try { await disconnected.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException) { }
            catch (Exception ex) { _onError(ex); }
            finally { lifetime.Cancel(); _slots.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        Task[] pending;
        lock (_clients) pending = _clients.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        _stop.Dispose();
        _slots.Dispose();
    }
}

public static class AutomationPipeClient
{
    public static async Task<AutomationResponse> CallAsync(AutomationInstance instance, string operation, object arguments,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", instance.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(TimeSpan.FromSeconds(3));
        await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var request = new AutomationRequest(operation, JsonSerializer.SerializeToElement(arguments, AutomationJson.Options));
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, AutomationJson.Options).AsMemory(), cancellationToken).ConfigureAwait(false);
        var response = await new BoundedLineReader(reader, 1024 * 1024).ReadAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AutomationResponse>(response ?? throw new IOException("GUI disconnected."), AutomationJson.Options)
            ?? throw new IOException("Invalid GUI response.");
    }
}
