using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SerialMonitor.Core.Automation;

public sealed record AutomationInstance(string InstanceId, string PipeName, int ProcessId, DateTimeOffset StartedAt)
{
    // Optional capability: older GUIs/clients keep using the ordinary endpoint.
    public string? ControlPipeName { get; init; }

    internal bool HasControlPipe => ControlPipeName == PipeName + "-control";
    internal static bool IsCancellation(string operation) => operation is "cancel_capture" or "sequence_cancel";
}

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
                    !instance.PipeName.All(c => char.IsLetterOrDigit(c) || c == '-') ||
                    (instance.ControlPipeName is not null && !instance.HasControlPipe)) continue;
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
    // Connections include incomplete requests and clients draining replies. They
    // must not consume the separate budget for executing GUI operations.
    private const int MaximumConnections = 64;
    // A headerless ordinary connection cannot be classified as a cancellation.
    // Reserve transport admission on a different name, not just execution slots.
    private const int MaximumControlConnections = 4;
    private readonly SemaphoreSlim _connections = new(MaximumConnections);
    private readonly SemaphoreSlim _controlConnections = new(MaximumControlConnections);
    private readonly SemaphoreSlim _requests = new(8);
    private readonly SemaphoreSlim _cancellations = new(1);
    private readonly List<Task> _clients = [];
    private readonly string _descriptorPath;
    private readonly Action<Exception> _onError;
    private Task? _loop;
    private readonly object _disposeGate = new();
    private readonly object _discoveryGate = new();
    private bool _stopping;
    private Task? _disposeTask;

    public AutomationPipeHost(string instanceId, Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> handle,
        Action<Exception> onError, string? directory = null)
    {
        _handle = handle;
        _onError = onError;
        using var process = Process.GetCurrentProcess();
        var pipeName = "serialmonitor-" + instanceId;
        Instance = new(instanceId, pipeName, Environment.ProcessId,
            new DateTimeOffset(process.StartTime.ToUniversalTime())) { ControlPipeName = pipeName + "-control" };
        var root = directory ?? AutomationDiscovery.DirectoryPath;
        Directory.CreateDirectory(root);
        _descriptorPath = Path.Combine(root, instanceId + ".json");
    }

    public AutomationInstance Instance { get; }

    public void Start()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null) throw new ObjectDisposedException(nameof(AutomationPipeHost));
            if (_loop is not null) throw new InvalidOperationException("Host already started.");
            _loop = Task.Run(ListenAsync);
        }
    }

    private async Task ListenAsync()
    {
        NamedPipeServerStream? ordinary = null, control = null;
        Task[] listeners = [];
        try
        {
            // Both endpoints exist before discovery advertises the capability.
            ordinary = CreatePipe(Instance.PipeName, MaximumConnections);
            control = CreatePipe(Instance.ControlPipeName!, MaximumControlConnections);
            listeners = [ListenEndpointAsync(ordinary, _connections, MaximumConnections, false),
                ListenEndpointAsync(control, _controlConnections, MaximumControlConnections, true)];
            ordinary = control = null; // Listener tasks own both initial pipes.
            await PublishDiscoveryAsync().ConfigureAwait(false);
            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            ordinary?.Dispose();
            control?.Dispose();
            if (WithdrawDiscovery() is { } withdrawalError) ReportError(withdrawalError);
            await _stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
    }

    private async Task PublishDiscoveryAsync()
    {
        var temporary = _descriptorPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(Instance, AutomationJson.Options), _stop.Token).ConfigureAwait(false);
            lock (_discoveryGate)
            {
                // A stop racing the asynchronous write must never republish a
                // host whose handlers/listeners are still finishing cleanup.
                if (!_stopping) File.Move(temporary, _descriptorPath, overwrite: true);
            }
        }
        finally { File.Delete(temporary); }
    }

    private Exception? WithdrawDiscovery()
    {
        lock (_discoveryGate)
        {
            Volatile.Write(ref _stopping, true);
            try { File.Delete(_descriptorPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex; }
        }
        return null;
    }

    private static NamedPipeServerStream CreatePipe(string name, int maximum) => new(name, PipeDirection.InOut, maximum,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenEndpointAsync(NamedPipeServerStream initial, SemaphoreSlim connections, int maximum, bool controlOnly)
    {
        NamedPipeServerStream? pipe = initial;
        try
        {
            while (!_stop.IsCancellationRequested && !Volatile.Read(ref _stopping))
            {
                await connections.WaitAsync(_stop.Token).ConfigureAwait(false);
                var transferred = false;
                try
                {
                    pipe ??= CreatePipe(controlOnly ? Instance.ControlPipeName! : Instance.PipeName, maximum);
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    var connected = pipe;
                    pipe = null;
                    transferred = true;
                    lock (_clients)
                    {
                        _clients.RemoveAll(task =>
                        {
                            if (!task.IsCompleted) return false;
                            if (task.IsFaulted) ReportError(task.Exception!);
                            return true;
                        });
                        _clients.Add(ServeAsync(connected, connections, controlOnly));
                    }
                }
                finally
                {
                    if (!transferred) { pipe?.Dispose(); pipe = null; connections.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ReportError(ex);
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        finally { pipe?.Dispose(); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, SemaphoreSlim connections, bool controlOnly)
    {
        try
        {
            using (pipe)
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            using (var reader = new Utf8ChunkReader(pipe))
            {
                if (Volatile.Read(ref _stopping)) return;
                Task<string?>? disconnected = null;
                try
                {
                    lifetime.CancelAfter(TimeSpan.FromSeconds(90));
                    var ioTimeout = TimeSpan.FromSeconds(controlOnly ? 1 : 5);
                    var lines = new BoundedLineReader(reader);
                    AutomationRequest request;
                    try
                    {
                        using var header = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        header.CancelAfter(ioTimeout);
                        var line = await lines.ReadAsync(header.Token).ConfigureAwait(false);
                        if (line is null) return;
                        request = AutomationJsonValidation.ParseRequest(line);
                    }
                    catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
                    {
                        var invalid = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(AutomationResponse.Fail("invalid_argument",
                            "Invalid automation request: " + ex.Message), AutomationJson.Options) + "\n");
                        await WriteReplyAsync(pipe, invalid, ioTimeout, lifetime.Token).ConfigureAwait(false);
                        return;
                    }
                    if (Volatile.Read(ref _stopping)) return;
                    disconnected = lines.ReadAsync(lifetime.Token);
                    if (disconnected.IsCompleted) return;
                    AutomationResponse response;
                    var isCancellation = AutomationInstance.IsCancellation(request.Operation);
                    var budget = isCancellation ? _cancellations : _requests;
                    if (controlOnly && !isCancellation)
                    {
                        response = AutomationResponse.Fail("invalid_argument", "The control pipe accepts only cancel_capture and sequence_cancel. This request was not executed.");
                    }
                    else if (!await budget.WaitAsync(0, lifetime.Token).ConfigureAwait(false))
                    {
                        // Do not queue TX for later, or replay it when capacity
                        // becomes free. This request has not entered the handler.
                        response = AutomationResponse.Fail("busy", "GUI request capacity is occupied. This request was not executed and did not send TX.");
                    }
                    else
                    {
                        try
                        {
                            if (Volatile.Read(ref _stopping)) return;
                            var work = _handle(request, lifetime.Token);
                            if (await Task.WhenAny(work, disconnected).ConfigureAwait(false) == disconnected)
                            {
                                await lifetime.CancelAsync().ConfigureAwait(false);
                                try { await work.ConfigureAwait(false); } catch (OperationCanceledException) { }
                                return;
                            }
                            response = await work.ConfigureAwait(false);
                        }
                        finally { budget.Release(); }
                    }
                    // No buffered writer: a broken pipe cannot trigger a second
                    // flush exception during disposal outside the request catch.
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, AutomationJson.Options) + "\n");
                    await WriteReplyAsync(pipe, bytes, ioTimeout, lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    await lifetime.CancelAsync().ConfigureAwait(false);
                    if (disconnected is not null)
                        try { await disconnected.ConfigureAwait(false); }
                        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException) { }
        catch (Exception ex) { ReportError(ex); }
        finally { connections.Release(); }
    }

    private static async Task WriteReplyAsync(NamedPipeServerStream pipe, byte[] bytes, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
    }

    private void ReportError(Exception error)
    {
        try { _onError(error); }
        catch (Exception reportingError)
        {
            Trace.TraceError("Automation error: {0}; error reporting failed: {1}", error, reportingError);
        }
    }

    // A caller's deadline bounds its wait, not the lifetime of resources still
    // used by a handler that ignores cancellation. Dispose those when it exits.
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Task cleanup;
        Exception? withdrawalError = null;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                // Withdrawal and admission shutdown cannot wait for listener
                // token callbacks: a request callback may block indefinitely.
                withdrawalError = WithdrawDiscovery();
                _disposeTask = DisposeCoreAsync();
                _ = _disposeTask.ContinueWith(task => ReportError(task.Exception!), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            cleanup = _disposeTask;
        }
        // Error callbacks may call StopAsync again. Publish the shared cleanup
        // task first, and never invoke external code while holding these locks.
        if (withdrawalError is not null) ReportError(withdrawalError);
        return cleanup.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await StopAsync(deadline.Token).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        // Cancellation callbacks belong to request handlers and can themselves
        // block. Do not run them synchronously on the window's closing thread.
        var canceled = _stop.CancelAsync();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        Task[] pending;
        lock (_clients) pending = _clients.ToArray();
        try
        {
            await Task.WhenAll(pending.Append(canceled)).ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
            _connections.Dispose();
            _controlConnections.Dispose();
            _requests.Dispose();
            _cancellations.Dispose();
        }
    }
}

public static class AutomationPipeClient
{
    public static async Task<AutomationResponse> CallAsync(AutomationInstance instance, string operation, object arguments,
        CancellationToken cancellationToken)
    {
        var pipeName = AutomationInstance.IsCancellation(operation) && instance.HasControlPipe ? instance.ControlPipeName! : instance.PipeName;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(TimeSpan.FromSeconds(3));
        var attempts = 0;
        try
        {
            // ConnectAsync queues even a zero-timeout probe onto a worker. Under
            // stdio/process load that queued work can outlive the deadline before
            // reaching an available pipe. A 1ms local probe also avoids Windows'
            // default wait for timeout=0. Back off asynchronously if unavailable.
            // Only connection is retried; write the request exactly once below.
            var delayMs = 4;
            while (true)
            {
                connect.Token.ThrowIfCancellationRequested();
                try { attempts++; pipe.Connect(1); connect.Token.ThrowIfCancellationRequested(); break; }
                catch (TimeoutException)
                {
                    await Task.Delay(delayMs + Random.Shared.Next(delayMs), connect.Token).ConfigureAwait(false);
                    delayMs = Math.Min(delayMs * 2, 32);
                }
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException or TimeoutException) && !cancellationToken.IsCancellationRequested)
        {
            throw new AutomationException("gui_connect_timeout",
                $"Could not connect to the GUI within 3 seconds ({attempts} connection probes). Its pipe may be busy or restarting. No request was sent and this call did not transmit any bytes.");
        }
        using var reader = new Utf8ChunkReader(pipe);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var request = new AutomationRequest(operation, JsonSerializer.SerializeToElement(arguments, AutomationJson.Options));
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, AutomationJson.Options).AsMemory(), cancellationToken).ConfigureAwait(false);
        var response = await new BoundedLineReader(reader, 1024 * 1024).ReadAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AutomationResponse>(response ?? throw new IOException("GUI disconnected."), AutomationJson.Options)
            ?? throw new IOException("Invalid GUI response.");
    }
}
