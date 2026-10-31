using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 3) throw new ArgumentException("Arguments: absolute serialctl path, GUI instance ID, evidence directory. Only the connected COM3 SERIAL-LAB/1 board is supported.");
var cli = Path.GetFullPath(args[0]);
var instance = args[1];
var directory = Path.GetFullPath(args[2]);
Directory.CreateDirectory(directory);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var token = deadline.Token;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
var evidence = new ConcurrentQueue<object>();
var pages = new ConcurrentQueue<object>();
var timings = new List<double>();
var tag = "admit-" + Guid.NewGuid().ToString("N")[..8];
var accepted = 0;
var busy = 0;
var calls = 0;
string? failure = null;
long before = -1, after = -1;
var watch = Stopwatch.StartNew();

async Task<JsonElement> Control(params string[] options)
{
    using var process = Start([.. options, "--instance", instance]);
    process.StandardInput.Close();
    var stdout = ReadOutput(process.StandardOutput);
    var stderr = ReadOutput(process.StandardError);
    try
    {
        await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
        var error = await stderr;
        if (error.Length != 0) throw new IOException(error);
        var response = JsonSerializer.Deserialize<JsonElement>(await stdout);
        if (process.ExitCode != (response.GetProperty("success").GetBoolean() ? 0 : 1)) throw new IOException("Unexpected CLI exit code.");
        evidence.Enqueue(new { options, response });
        return response;
    }
    finally { if (!process.HasExited) process.Kill(true); }
}

Process Start(string[] options)
{
    var info = new ProcessStartInfo(cli)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
    };
    foreach (var option in options) info.ArgumentList.Add(option);
    return Process.Start(info)!;
}

async Task<string> CaptureText(JsonElement response, string name)
{
    Require(response.GetProperty("success").GetBoolean(), response.ToString());
    var capture = response.GetProperty("data").GetProperty("capture");
    Require(capture.GetProperty("sendStatus").GetString() == "sent", "TX was not confirmed: " + response);
    Require(capture.GetProperty("captureStatus").GetString() == "completed" &&
        !capture.GetProperty("rxGapDetected").GetBoolean() && capture.GetProperty("serialErrors").GetInt64() == 0 &&
        capture.GetProperty("droppedBytes").GetInt64() == 0, "RX gap or incomplete capture: " + response);
    var bytes = await File.ReadAllBytesAsync(capture.GetProperty("rawFile").GetString()!, token);
    Require(bytes.Length == capture.GetProperty("storedBytes").GetInt64(), "Incomplete evidence read.");
    var path = Path.Combine(directory, name + ".bin");
    await File.WriteAllBytesAsync(path, bytes, token);
    pages.Enqueue(new { name, captureId = capture.GetProperty("captureId").GetString(), bytes = bytes.Length, path });
    return Encoding.UTF8.GetString(bytes);
}

async Task<string> Request(string command, string name)
{
    var response = await Control("request", "--port", "COM3", "--text", command, "--ending", "crlf",
        "--duration-ms", "300", "--preview-bytes", "128", "--format", "text");
    return await CaptureText(response, name);
}

try
{
    var state = (await Control("status")).GetProperty("data");
    Require(state.GetProperty("connected").GetBoolean() && state.GetProperty("port").GetString() == "COM3" &&
        state.GetProperty("baudRate").GetInt32() == 115200 && state.GetProperty("dataBits").GetInt32() == 8 &&
        state.GetProperty("parity").GetString() == "None" && state.GetProperty("stopBits").GetString() == "One" &&
        state.GetProperty("handshake").GetString() == "None" && !state.GetProperty("busy").GetBoolean(), "Require idle COM3 / 115200 / 8N1 / no handshake.");
    Require((await Request("ID?", "identity")).Contains("ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1"), "Firmware identity mismatch; no load commands were sent.");
    before = CommandCount(await Request("STATUS?", "status-before"));
    await using var first = await Rpc.StartAsync(Start(["mcp", "--instance", instance]), token);
    await using var second = await Rpc.StartAsync(Start(["mcp", "--instance", instance]), token);
    await using var third = await Rpc.StartAsync(Start(["mcp", "--instance", instance]), token);
    Rpc[] sessions = [first, second, third];
    for (var round = 0; round < 20; round++)
    {
        var started = Stopwatch.GetTimestamp();
        var pending = new List<(string? CommandTag, Task<JsonElement> Reply)>();
        for (var session = 0; session < sessions.Length; session++)
        {
            for (var item = 0; item < 6; item++)
            {
                var nonce = item == 4 ? $"{tag}-{round:D2}-m{session}" : null;
                pending.Add((nonce, sessions[session].ToolAsync(nonce is null ? "serial_get_status" : "serial_send_and_capture",
                    nonce is null ? new { } : new { command = "PING " + nonce, port = "COM3", txMode = "text", lineEnding = "crlf", durationMs = 200, previewBytes = 128, format = "text" }, token)));
            }
        }
        for (var item = 0; item < 8; item++)
        {
            var nonce = item < 4 ? null : $"{tag}-{round:D2}-c{item}";
            pending.Add((nonce, nonce is null ? Control("status") : Control("request", "--port", "COM3", "--text", "PING " + nonce,
                "--ending", "crlf", "--duration-ms", "200", "--preview-bytes", "128", "--format", "text")));
        }
        var responses = await Task.WhenAll(pending.Select(item => item.Reply));
        for (var index = 0; index < responses.Length; index++)
        {
            var response = responses[index];
            calls++;
            if (!response.GetProperty("success").GetBoolean())
            {
                Require(response.GetProperty("error").GetProperty("code").GetString() == "busy", response.ToString());
                busy++;
            }
            else if (pending[index].CommandTag is { } nonce)
            {
                var rx = await CaptureText(response, nonce);
                Require(Regex.Matches(rx, "(?m)^PONG " + Regex.Escape(nonce) + "\\r?$").Count == 1, "Missing or duplicate PONG for " + nonce + ": " + rx);
                accepted++;
            }
            evidence.Enqueue(new { round, index, commandTag = pending[index].CommandTag, response });
        }
        timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Console.WriteLine($"Round {round + 1}/20: {calls} calls; {accepted} confirmed unique PONGs; {busy} busy replies.");
    }
    after = CommandCount(await Request("STATUS?", "status-after"));
    Require(after - before == accepted + 1, $"Device command counter mismatch: delta {after - before}, expected {accepted + 1}. No TX is retried.");
    var final = (await Control("status")).GetProperty("data");
    Require(final.GetProperty("connected").GetBoolean() && !final.GetProperty("busy").GetBoolean() && final.GetProperty("serialErrors").GetInt64() == 0, "Final GUI state is not healthy.");
}
catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(failure); }
finally
{
    timings.Sort();
    await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
    {
        port = "COM3", baudRate = 115200, instanceId = instance, command = "PING <unique nonce>", lineEnding = "crlf",
        durationMs = 200, calls, accepted, busy, commandCountBefore = before, commandCountAfter = after,
        expectedDelta = accepted + 1, elapsedSeconds = watch.Elapsed.TotalSeconds,
        roundP95Ms = timings.Count == 0 ? 0 : timings[(int)Math.Ceiling(timings.Count * .95) - 1],
        failure, captures = pages.ToArray(), responses = evidence.ToArray()
    }, new JsonSerializerOptions(json) { WriteIndented = true }));
}
return failure is null ? 0 : 1;

static Task<string> ReadOutput(StreamReader reader) => Task.Factory.StartNew(reader.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static long CommandCount(string text)
{
    var match = Regex.Match(text, @"\bcommands=(\d+)");
    Require(match.Success, "Missing device command counter: " + text);
    Require(Regex.IsMatch(text, @"parse_errors=0 rx_drops=0 uart_errors=0 tx_rejected=0"), "Device reported unintended UART/parser errors: " + text);
    return long.Parse(match.Groups[1].Value);
}

sealed class Rpc : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderr;
    private readonly Task _pump;
    private readonly SemaphoreSlim _input = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _id;
    private Rpc(Process process)
    {
        _process = process;
        _stderr = Task.Factory.StartNew(process.StandardError.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _pump = Task.Factory.StartNew(() =>
        {
            try
            {
                while (_process.StandardOutput.ReadLine() is { } line)
                {
                    var reply = JsonSerializer.Deserialize<JsonElement>(line);
                    if (!_pending.TryRemove(reply.GetProperty("id").GetInt32(), out var completion)) throw new IOException("Unexpected or duplicate MCP reply.");
                    completion.SetResult(reply);
                }
            }
            finally { foreach (var pair in _pending) pair.Value.TrySetException(new IOException("MCP process closed.")); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public static async Task<Rpc> StartAsync(Process process, CancellationToken token)
    {
        var rpc = new Rpc(process);
        try
        {
            var initialized = await rpc.CallAsync("initialize", new { protocolVersion = "2025-06-18" }, token);
            if (!initialized.TryGetProperty("result", out _)) throw new IOException(initialized.ToString());
            await rpc.WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, token);
            var tools = await rpc.CallAsync("tools/list", new { }, token);
            if (!tools.GetProperty("result").GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "serial_send_and_capture"))
                throw new IOException("MCP capture tool is unavailable.");
            return rpc;
        }
        catch { await rpc.DisposeAsync(); throw; }
    }
    public async Task<JsonElement> ToolAsync(string name, object arguments, CancellationToken token)
    {
        var reply = await CallAsync("tools/call", new { name, arguments }, token);
        if (reply.TryGetProperty("error", out _)) throw new IOException(reply.ToString());
        return reply.GetProperty("result").GetProperty("structuredContent");
    }
    private async Task<JsonElement> CallAsync(string method, object args, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _id);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate local MCP id.");
        await WriteAsync(new { jsonrpc = "2.0", id, method, @params = args }, token);
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
    }
    private async Task WriteAsync(object value, CancellationToken token)
    {
        await _input.WaitAsync(token);
        try { await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token); }
        finally { _input.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await _pump;
            var error = await _stderr;
            if (_process.ExitCode != 0 || error.Length != 0) throw new IOException("MCP failed: " + error);
        }
        finally { if (!_process.HasExited) _process.Kill(true); _process.Dispose(); _input.Dispose(); }
    }
}
