using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class CliOutputIntegrityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedStdoutCannotBeReportedAsASuccessfulEmptyReply(bool transmit)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(CliOutputIntegrityTests), Guid.NewGuid().ToString("N"));
        await using var captures = new RxCaptureService(root);
        var device = new Device(captures);
        await using var controller = new AutomationController(device, captures);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AutomationResponse? completed = null;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), async (request, token) =>
        {
            completed = await controller.HandleAsync(request, token);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return completed;
        }, _ => { });
        host.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!AutomationDiscovery.List().Any(item => item.InstanceId == host.Instance.InstanceId)) await Task.Delay(10, timeout.Token);
        var assembly = typeof(CliCommandInput).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in transmit
            ? new[] { assembly, "request", "--text", "mock only", "--duration-ms", "0", "--instance", host.Instance.InstanceId }
            : new[] { assembly, "status", "--instance", host.Instance.InstanceId }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        child.StandardInput.Close();
        var errors = ProcessStreamReader.ReadToEndAsync(child.StandardError);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            // The consumer disappears after the operation was accepted but before
            // the CLI writes its reply. No bytes can reach the consumer now.
            child.StandardOutput.Dispose();
            release.TrySetResult();
            await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, child.ExitCode);
            Assert.Contains("output", await errors, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(completed);
            Assert.True(completed.Success);
            Assert.Equal(transmit ? 1 : 0, device.Sends);
            if (transmit)
            {
                var capture = captures.GetResult(completed.Data.GetProperty("capture").GetProperty("captureId").GetString()!);
                Assert.Equal("sent", capture.SendStatus);
                Assert.Equal(new byte[] { 0, 255 }, await File.ReadAllBytesAsync(capture.RawFile));
            }
        }
        finally
        {
            release.TrySetResult(); if (!child.HasExited) child.Kill(true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task McpClosesAndReportsBrokenStdoutWhileStdinRemainsOpen()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), async (_, token) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token);
            return AutomationResponse.Ok(new { connected = true });
        }, _ => { });
        host.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!AutomationDiscovery.List().Any(item => item.InstanceId == host.Instance.InstanceId)) await Task.Delay(10, timeout.Token);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { typeof(CliCommandInput).Assembly.Location, "mcp", "--instance", host.Instance.InstanceId }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var errors = ProcessStreamReader.ReadToEndAsync(child.StandardError);
        try
        {
            await child.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
            Assert.Contains("serverInfo", await child.StandardOutput.ReadLineAsync(timeout.Token));
            await child.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await child.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"serial_get_status\",\"arguments\":{}}}");
            await entered.Task.WaitAsync(timeout.Token);
            child.StandardOutput.Dispose(); release.TrySetResult();
            await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, child.ExitCode);
            Assert.Contains("output", await errors, StringComparison.OrdinalIgnoreCase);
        }
        finally { release.TrySetResult(); if (!child.HasExited) child.Kill(true); }
    }

    private sealed class Device(RxCaptureService captures) : IAutomationDevice
    {
        public int Sends;
        public CaptureConnection Connection => new(true, 1, 0);
        public Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (operation == "send") { Interlocked.Increment(ref Sends); captures.Offer(1, [0, 255], Stopwatch.GetTimestamp()); }
            return Task.FromResult<object>(new AutomationDeviceState(true, "MOCK", 115200, 1, "Utf8", 0, false, null));
        }
    }
}
