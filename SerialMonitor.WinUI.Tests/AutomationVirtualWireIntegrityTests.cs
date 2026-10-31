using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

// Only the registry-verified com0com pair is permitted. No physical port or live
// GUI is selected, and the test's instance/profile/captures are isolated.
public sealed class AutomationVirtualWireIntegrityTests(ITestOutputHelper output)
{
    [Com0comPair0Fact]
    public async Task CliAndMcpCarryExactModbusBinaryAndTerminalBytesThroughTheWindowsDriver()
    {
        VerifyVirtualPort("CNCA0"); VerifyVirtualPort("CNCB0");
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationVirtualWireIntegrityTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        Task<JsonElement>? pendingCall = null;
        try
        {
            await using var serial = new SerialService();
            await using var peer = new SerialService();
            await serial.ConnectAsync(new() { PortName = "CNCA0" }, new() { IdleTimeoutMs = 5 }, token);
            await peer.ConnectAsync(new() { PortName = "CNCB0" }, new() { IdleTimeoutMs = 5 }, token);
            await using var captures = new RxCaptureService(Path.Combine(root, "captures"));
            serial.CaptureBytesReceived += (generation, chunk) => captures.Offer(generation, chunk.Bytes, chunk.ReceivedTimestamp);
            var device = new WireDevice(serial);
            await using var controller = new AutomationController(device, captures);
            await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), controller.HandleAsync,
                ex => throw new InvalidOperationException("Virtual automation host failed.", ex));
            host.Start();
            while (!AutomationDiscovery.List().Any(instance => instance.InstanceId == host.Instance.InstanceId))
                await Task.Delay(10, token);

            byte[] modbusRequest = [1, 3, 0, 0, 0, 1, 0x84, 0x0A];
            byte[] modbusResponse = [1, 3, 2, 0, 0x2A, 0x39, 0x9B];
            Assert.Equal((ushort)0x0A84, Crc16(modbusRequest.AsSpan(0, 6)));
            Assert.Equal((ushort)0x9B39, Crc16(modbusResponse.AsSpan(0, 5)));
            var allBytes = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
            const string terminal = " \t\0\u0001\u001b\"\\가🙂\r\n ";
            var cases = new List<(string Command, string Mode, string Ending, byte[] Tx, byte[] Rx)>
            {
                ("01 03 00 00 00 01 84 0A", "hex", "crlf", modbusRequest, modbusResponse),
                (string.Join(",\t", allBytes.Select(value => "0x" + value.ToString("x2"))), "hex", "lf", allBytes, allBytes.Reverse().ToArray())
            };
            foreach (var (ending, suffix) in new[] { ("none", ""), ("cr", "\r"), ("lf", "\n"), ("crlf", "\r\n") })
                cases.Add((terminal, "text", ending, Encoding.UTF8.GetBytes(terminal + suffix), Encoding.UTF8.GetBytes("\0응답🙂\r\n> ")));

            foreach (var mcp in new[] { false, true })
            {
                if (mcp)
                {
                    await serial.DisconnectAsync(token); await peer.DisconnectAsync(token);
                    await serial.ConnectAsync(new() { PortName = "CNCA0" }, new() { UseNativeIdleTimeout = true, IdleTimeoutMs = 5 }, token);
                    await peer.ConnectAsync(new() { PortName = "CNCB0" }, new() { UseNativeIdleTimeout = true, IdleTimeoutMs = 5 }, token);
                    Assert.True(serial.UsesNativeReceiveIdleTimeout);
                }
                await using var rpc = mcp ? await McpProcess.StartAsync(host.Instance.InstanceId, token) : null;
                foreach (var item in cases)
                {
                    output.WriteLine($"{(mcp ? "MCP" : "CLI")} {item.Mode}/{item.Ending}, TX {item.Tx.Length} bytes");
                    Task<JsonElement> call;
                    if (rpc is not null)
                        call = rpc.CallAsync("serial_send_and_capture", new { command = item.Command, txMode = item.Mode,
                            lineEnding = item.Ending, durationMs = 250, format = "hex", previewBytes = 4 }, token);
                    else
                    {
                        var input = item.Mode == "hex" ? "--hex" : "--text-file";
                        var value = item.Command;
                        if (item.Mode == "text")
                        {
                            value = Path.Combine(root, "command.txt");
                            await File.WriteAllTextAsync(value, item.Command, new UTF8Encoding(false, true), token);
                        }
                        call = RunCliAsync(["request", input, value, "--ending", item.Ending, "--duration-ms", "250",
                            "--format", "hex", "--preview-bytes", "4", "--instance", host.Instance.InstanceId], token);
                    }
                    pendingCall = call;
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                    var receive = ReadExactlyAsync(peer, item.Tx.Length, readDeadline.Token);
                    if (await Task.WhenAny(call, receive) == call) _ = await call;
                    Assert.Equal(item.Tx, await receive);
                    // A response spans several native reads and includes arbitrary
                    // binary/control bytes, independently of GUI text decoding.
                    var split = Math.Min(3, item.Rx.Length);
                    await peer.SendBytesAsync(item.Rx[..split], "", token);
                    await Task.Delay(15, token);
                    await peer.SendBytesAsync(item.Rx[split..], "", token);
                    var response = await call;
                    pendingCall = null;
                    Assert.True(response.GetProperty("success").GetBoolean(), response.GetRawText());
                    var data = response.GetProperty("data");
                    var capture = data.GetProperty("capture");
                    Assert.Equal("sent", capture.GetProperty("sendStatus").GetString());
                    Assert.Equal("completed", capture.GetProperty("captureStatus").GetString());
                    Assert.False(capture.GetProperty("rxGapDetected").GetBoolean());
                    Assert.Equal(item.Rx.Length, capture.GetProperty("storedBytes").GetInt64());
                    Assert.Equal(item.Rx, await File.ReadAllBytesAsync(capture.GetProperty("rawFile").GetString()!, token));
                    var page = data.GetProperty("page");
                    var combined = new StringBuilder(page.GetProperty("text").GetString());
                    while (page.GetProperty("hasMore").GetBoolean())
                    {
                        var id = page.GetProperty("captureId").GetString()!;
                        var offset = page.GetProperty("nextOffset").GetInt32();
                        var next = rpc is not null
                            ? await rpc.CallAsync("serial_read_capture", new { captureId = id, offset, limit = 137 }, token)
                            : await RunCliAsync(["read", "--capture", id, "--offset", offset.ToString(), "--limit", "137", "--instance", host.Instance.InstanceId], token);
                        Assert.True(next.GetProperty("success").GetBoolean(), next.GetRawText());
                        page = next.GetProperty("data").GetProperty("page");
                        combined.Append(page.GetProperty("text").GetString());
                    }
                    Assert.Equal(Convert.ToHexString(item.Rx), combined.ToString());
                    Assert.False(device.Reserved);
                }
            }
            Assert.Equal(cases.Count * 2, device.SendCount);
            Assert.Equal(0, serial.SerialFrameErrorCount + serial.SerialParityErrorCount + serial.SerialOverrunErrorCount + serial.SerialRxOverErrorCount);
        }
        finally
        {
            deadline.Cancel();
            if (pendingCall is not null) await Record.ExceptionAsync(() => pendingCall);
            Directory.Delete(root, true);
        }
    }

    private static void VerifyVirtualPort(string port)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\com0com\port\" + port);
        Assert.NotNull(key); Assert.Equal("com0com", key.GetValue("Service"));
        using var parameters = key.OpenSubKey("Device Parameters");
        Assert.Equal(port, parameters?.GetValue("PortName"));
    }

    [Com0comPair0Fact]
    public async Task RepeatedMixedSequencesKeepStepModesRxRangesAndExclusiveOwnershipThroughCliAndMcp()
    {
        VerifyVirtualPort("CNCA0"); VerifyVirtualPort("CNCB0");
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationVirtualWireIntegrityTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        Task? emulating = null;
        try
        {
            await using var serial = new SerialService();
            await using var peer = new SerialService();
            await serial.ConnectAsync(new() { PortName = "CNCA0" }, new(), token);
            await peer.ConnectAsync(new() { PortName = "CNCB0" }, new(), token);
            await using var captures = new RxCaptureService(Path.Combine(root, "captures"));
            serial.CaptureBytesReceived += (generation, chunk) => captures.Offer(generation, chunk.Bytes, chunk.ReceivedTimestamp);
            var device = new WireDevice(serial);
            await using var controller = new AutomationController(device, captures);
            await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), controller.HandleAsync, _ => { });
            host.Start();
            while (!AutomationDiscovery.List().Any(instance => instance.InstanceId == host.Instance.InstanceId)) await Task.Delay(10, token);
            var definition = new AutomationSequenceDefinition("mixed wire", 8,
            [
                new("HEX", "00 FF 11 13 0D 0A", "hex", "crlf", 500, 10),
                new("Terminal", " \t가🙂\0 ", "text", "lf", 500, 10)
            ]);
            byte[][] expectedTx = [[0, 255, 17, 19, 13, 10], Encoding.UTF8.GetBytes(" \t가🙂\0 \n")];
            foreach (var mcp in new[] { false, true })
            {
                output.WriteLine($"{(mcp ? "MCP" : "CLI")} mixed sequence: 8 repeats, then cancellation after one TX");
                await using var rpc = mcp ? await McpProcess.StartAsync(host.Instance.InstanceId, token) : null;
                var path = Path.Combine(root, "sequence.json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition, AutomationJson.Options), new UTF8Encoding(false), token);
                var saved = rpc is not null
                    ? await rpc.CallAsync("serial_sequence_save", new { sequence = definition, overwrite = true }, token)
                    : await RunCliAsync(["sequence", "save", "--file", path, "--overwrite", "true", "--instance", host.Instance.InstanceId], token);
                Assert.True(saved.GetProperty("success").GetBoolean(), saved.GetRawText());
                var started = rpc is not null
                    ? await rpc.CallAsync("serial_sequence_start", new { name = definition.Name }, token)
                    : await RunCliAsync(["sequence", "run", "--name", definition.Name, "--instance", host.Instance.InstanceId], token);
                Assert.True(started.GetProperty("success").GetBoolean(), started.GetRawText());
                var id = started.GetProperty("data").GetProperty("runId").GetString()!;
                var expectedRx = new List<byte>();
                emulating = Task.Run(async () =>
                {
                    for (var index = 0; index < 16; index++)
                    {
                        Assert.Equal(expectedTx[index % 2], await ReadExactlyAsync(peer, expectedTx[index % 2].Length, token));
                        var response = new byte[] { (byte)index, 0, 255, 17, 19 }.Concat(Encoding.UTF8.GetBytes("응답🙂\r\n")).ToArray();
                        expectedRx.AddRange(response);
                        await peer.SendBytesAsync(response[..2], "", token);
                        await Task.Delay(5, token);
                        await peer.SendBytesAsync(response[2..], "", token);
                    }
                }, token);
                foreach (var mode in new[] { "hex", "terminal" })
                {
                    var changed = rpc is not null
                        ? await rpc.CallAsync("serial_set_mode", new { mode }, token)
                        : await RunCliAsync(["mode", "--set", mode, "--instance", host.Instance.InstanceId], token, allowFailure: true);
                    Assert.False(changed.GetProperty("success").GetBoolean());
                    Assert.Equal("busy", changed.GetProperty("error").GetProperty("code").GetString());
                }
                await emulating; emulating = null;
                var done = await WaitForRunAsync(controller, id, token);
                Assert.Equal("completed", done.State);
                Assert.Equal(16, done.CompletedSteps);
                Assert.Equal("mixed", done.Capture!.TxMode);
                Assert.Equal("mixed", done.Capture.LineEnding);
                // Raw RX is the byte-integrity contract. A response delayed by
                // Windows scheduling may span an observation window and delay;
                // delay bytes remain in the full capture without overlapping
                // or being duplicated into two step ranges.
                Assert.Equal(expectedRx, await File.ReadAllBytesAsync(done.Capture!.RawFile, token));
                for (var index = 0; index < 16; index++)
                {
                    var step = done.Steps[index];
                    Assert.Equal(index % 2 == 0 ? "hex" : "text", step.TxMode);
                    Assert.Equal(index % 2 == 0 ? "none" : "lf", step.LineEnding);
                    Assert.InRange(step.RxStartOffset, index == 0 ? 0 : done.Steps[index - 1].RxEndOffset, done.Capture.StoredBytes);
                    Assert.InRange(step.RxEndOffset, step.RxStartOffset, done.Capture.StoredBytes);
                    Assert.Equal("sent", step.SendStatus);
                }
                var read = rpc is not null
                    ? await rpc.CallAsync("serial_read_capture", new { captureId = done.Capture.CaptureId }, token)
                    : await RunCliAsync(["read", "--capture", done.Capture.CaptureId, "--instance", host.Instance.InstanceId], token);
                Assert.Equal("hex", read.GetProperty("data").GetProperty("page").GetProperty("format").GetString());
                Assert.Equal(Convert.ToHexString(expectedRx.ToArray()), read.GetProperty("data").GetProperty("page").GetProperty("text").GetString());
                Assert.False(device.Reserved);

                device.Definition = definition with { RepeatCount = 1, Steps = definition.Steps.Select(step => step with { CaptureMs = 60000 }).ToArray() };
                started = rpc is not null
                    ? await rpc.CallAsync("serial_sequence_start", new { name = definition.Name }, token)
                    : await RunCliAsync(["sequence", "run", "--name", definition.Name, "--instance", host.Instance.InstanceId], token);
                id = started.GetProperty("data").GetProperty("runId").GetString()!;
                Assert.Equal(expectedTx[0], await ReadExactlyAsync(peer, expectedTx[0].Length, token));
                await peer.SendBytesAsync([0, 255, 0xAA], "", token);
                await Task.Delay(20, token);
                var canceled = rpc is not null
                    ? await rpc.CallAsync("serial_sequence_cancel", new { runId = id }, token)
                    : await RunCliAsync(["sequence", "cancel", "--run", id, "--instance", host.Instance.InstanceId], token);
                Assert.True(canceled.GetProperty("success").GetBoolean(), canceled.GetRawText());
                done = await WaitForRunAsync(controller, id, token);
                Assert.Equal("canceled", done.State);
                Assert.Single(done.Steps);
                Assert.Equal("sent", done.Steps[0].SendStatus);
                Assert.Equal(new byte[] { 0, 255, 0xAA }, await File.ReadAllBytesAsync(done.Capture!.RawFile, token));
                Assert.False(device.Reserved);
            }
            Assert.Equal(34, device.SendCount);
            Assert.False(peer.ReceivedBytes.TryRead(out _));
        }
        finally
        {
            deadline.Cancel();
            if (emulating is not null) await Record.ExceptionAsync(() => emulating);
            Directory.Delete(root, true);
        }
    }

    private static async Task<AutomationSequenceRun> WaitForRunAsync(AutomationController controller, string id, CancellationToken token)
    {
        while (true)
        {
            var response = await controller.HandleAsync(new("sequence_status", JsonSerializer.SerializeToElement(new { runId = id }, AutomationJson.Options)), token);
            Assert.True(response.Success, response.Error?.Message);
            var run = response.Data.Deserialize<AutomationSequenceRun>(AutomationJson.Options)!;
            if (run.State != "running") return run;
            await Task.Delay(10, token);
        }
    }

    private static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xA001));
        }
        return crc;
    }

    private static async Task<byte[]> ReadExactlyAsync(SerialService serial, int count, CancellationToken token)
    {
        var result = new List<byte>();
        while (result.Count < count) result.AddRange((await serial.ReceivedBytes.ReadAsync(token)).Bytes);
        Assert.Equal(count, result.Count);
        return result.ToArray();
    }

    private sealed class WireDevice(SerialService serial) : IAutomationDevice
    {
        public bool Reserved { get; private set; }
        public int SendCount { get; private set; }
        public AutomationSequenceDefinition? Definition { get; set; }
        private string _mode = "terminal";
        public CaptureConnection Connection => new(serial.IsConnected, serial.ReceiveSessionGeneration, 0);
        public async Task<object> InvokeAsync(string operation, JsonElement args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            switch (operation)
            {
                case "reserve": if (Reserved) throw new AutomationException("busy", "Sequence owns TX."); Reserved = true; break;
                case "release": Reserved = false; break;
                case "sequence_save": Definition = AutomationSequenceValidation.Parse(args.GetProperty("sequence")); break;
                case "sequence_get": return Definition!;
                case "set_mode": _mode = args.String("mode")!; break;
                case "validate_send": _ = Encode(args); break;
                case "send": Assert.True(Reserved); await serial.SendBytesAsync(Encode(args), "", token); SendCount++; _mode = _mode == "hex" ? "terminal" : "hex"; break;
            }
            return new AutomationDeviceState(serial.IsConnected, "CNCA0", 115200, serial.ReceiveSessionGeneration, "Utf8", 0, Reserved, null, Mode: _mode);
        }
        private static byte[] Encode(JsonElement args) => TxPayloadEncoder.Encode(args.String("command")!,
            args.String("txMode", "text") == "hex" ? TxSendMode.Hex : TxSendMode.Terminal,
            Enum.Parse<TxLineEndingMode>(args.String("lineEnding", "crlf")!, true));
    }

    private static Process StartCli(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(typeof(CliCommandInput).Assembly.Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task<JsonElement> RunCliAsync(string[] args, CancellationToken token, bool allowFailure = false)
    {
        using var child = StartCli(args);
        try
        {
            var output = child.StandardOutput.ReadToEndAsync(token);
            var error = child.StandardError.ReadToEndAsync(token);
            await child.WaitForExitAsync(token);
            Assert.Equal("", await error);
            var response = JsonSerializer.Deserialize<JsonElement>(await output);
            Assert.True(child.ExitCode == 0 || allowFailure, response.GetRawText());
            return response;
        }
        finally { if (!child.HasExited) child.Kill(true); }
    }

    private sealed class McpProcess(Process child) : IAsyncDisposable
    {
        private int _id = 1;
        public static async Task<McpProcess> StartAsync(string instance, CancellationToken token)
        {
            var rpc = new McpProcess(StartCli("mcp", "--instance", instance));
            await rpc.WriteAsync(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } }, token);
            _ = await rpc.ReadAsync(token);
            await rpc.WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, token);
            return rpc;
        }
        public async Task<JsonElement> CallAsync(string name, object arguments, CancellationToken token)
        {
            var id = ++_id;
            await WriteAsync(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } }, token);
            var response = await ReadAsync(token);
            Assert.Equal(id, response.GetProperty("id").GetInt32());
            return response.GetProperty("result").GetProperty("structuredContent");
        }
        private Task WriteAsync(object value, CancellationToken token) => child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, AutomationJson.Options).AsMemory(), token);
        private async Task<JsonElement> ReadAsync(CancellationToken token) =>
            JsonSerializer.Deserialize<JsonElement>(await child.StandardOutput.ReadLineAsync(token) ?? throw new IOException("MCP exited before replying."));
        public async ValueTask DisposeAsync()
        {
            child.StandardInput.Close();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { if (!child.HasExited) child.Kill(true); child.Dispose(); }
        }
    }
}
