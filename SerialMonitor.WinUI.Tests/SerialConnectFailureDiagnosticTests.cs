using System.ComponentModel;
using System.Text.Json;
using SerialMonitor.Core.Diagnostics;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialConnectFailureDiagnosticTests
{
    private sealed class Probe(Func<string, CancellationToken, Task<PortOwnerProbeReport>> find) : IPortOwnerProbe
    {
        public int Calls;
        public Task<PortOwnerProbeReport> FindAsync(string port, CancellationToken token) { Calls++; return find(port, token); }
    }

    [Fact]
    public async Task MissingPort_DoesNotEnumerateProcesses()
    {
        var probe = new Probe((_, _) => throw new Exception("Must not scan"));
        var result = await new SerialConnectFailureDiagnostics(probe).DiagnoseAsync("COM7", new Win32Exception(2), default);
        Assert.Equal(0, probe.Calls); Assert.Equal(SerialConnectFailureKind.PortNotFound, result.Failure.Kind);
        Assert.Equal(PortOwnerProbeStatus.NotNeeded, result.OwnerProbe.Status);
    }

    [Fact]
    public async Task ConfirmedOwner_AddsNamePidAndRawErrorToTheExistingFailureMessage()
    {
        var probe = new Probe((port, _) => Task.FromResult(new PortOwnerProbeReport(1, port, PortOwnerProbeStatus.Found, [new(1234, "putty.exe")])));
        var result = await new SerialConnectFailureDiagnostics(probe).DiagnoseAsync("COM7", new Win32Exception(5, "fixture raw error"), default);
        var message = ConnectFailurePresentation.Describe(result);
        Assert.Equal(1, probe.Calls); Assert.Contains("putty.exe", message); Assert.Contains("PID 1234", message);
        Assert.Contains("fixture raw error", message); Assert.Contains("0x00000005", message);
        Assert.Contains("putty.exe", ConnectFailurePresentation.Summary(result));
    }

    [Theory]
    [InlineData(PortOwnerProbeStatus.Limited)]
    [InlineData(PortOwnerProbeStatus.TimedOut)]
    [InlineData(PortOwnerProbeStatus.Unavailable)]
    [InlineData(PortOwnerProbeStatus.NotIdentified)]
    public async Task UnidentifiedOwner_DoesNotInventAProcessOrClaimThePortIsFree(PortOwnerProbeStatus status)
    {
        var probe = new Probe((port, _) => Task.FromResult(PortOwnerProbeReport.Empty(port, status)));
        var result = await new SerialConnectFailureDiagnostics(probe).DiagnoseAsync("COM7", new UnauthorizedAccessException("fixture access"), default);
        var message = ConnectFailurePresentation.Describe(result);
        Assert.Empty(result.OwnerProbe.Owners); Assert.DoesNotContain("PID ", message);
        Assert.Contains("fixture access", message); Assert.DoesNotContain("Programs confirmed", message);
    }

    [Fact]
    public async Task PipelineStartupFailure_IsNotMisdiagnosedAsPortOwnership()
    {
        var probe = new Probe((_, _) => throw new Exception("Must not scan"));
        var result = await new SerialConnectFailureDiagnostics(probe).DiagnoseAsync("COM7", new UnauthorizedAccessException("log startup failed"), default, openingPort: false);
        Assert.Equal(0, probe.Calls); Assert.Equal(SerialConnectFailureKind.SessionStartupFailed, result.Failure.Kind);
        Assert.Contains("log startup failed", ConnectFailurePresentation.Describe(result));
    }

    [Fact]
    public async Task ProbeFailure_CannotHideTheOriginalConnectionError()
    {
        var probe = new Probe((_, _) => throw new IOException("probe failed"));
        var result = await new SerialConnectFailureDiagnostics(probe).DiagnoseAsync("COM7", new UnauthorizedAccessException("original failure"), default);
        Assert.Equal(PortOwnerProbeStatus.Unavailable, result.OwnerProbe.Status);
        Assert.Contains("original failure", ConnectFailurePresentation.Describe(result));
    }

    [Fact]
    public async Task CanceledLookup_IsNotPresentedAsAnActualConnectionFailure()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new Probe(async (port, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return PortOwnerProbeReport.Empty(port, PortOwnerProbeStatus.NotIdentified); });
        var work = new SerialConnectFailureDiagnostics(probe).DiagnoseAsync("COM7", new UnauthorizedAccessException(), stop.Token);
        await entered.Task; stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
    }

    [Fact]
    public void WorkerStartsHidden_AndNeverUsesShellOrElevation()
    {
        var start = WindowsPortOwnerProbeClient.CreateStartInfo(Path.Combine(Path.GetTempPath(), "worker.exe"), "COM7");
        Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Equal("COM7", start.ArgumentList[0]); Assert.Equal(Environment.ProcessId.ToString(), start.ArgumentList[1]); Assert.Equal("", start.Verb);
    }

    [Theory]
    [InlineData(2, "COM7", 1234)]
    [InlineData(1, "COM8", 1234)]
    [InlineData(1, "COM7", -1)]
    public void InvalidWorkerReply_IsRejected(int version, string port, int pid)
    {
        var json = JsonSerializer.Serialize(new PortOwnerProbeReport(version, port, PortOwnerProbeStatus.Found, [new(pid, "terminal.exe")]), PortOwnerProbeProtocol.JsonOptions);
        Assert.Throws<InvalidDataException>(() => WindowsPortOwnerProbeClient.ParseReply(json, "COM7", "COM7"));
    }

    [Fact]
    public void WorkerReply_WithNoOwnerCannotClaimFound()
    {
        var json = JsonSerializer.Serialize(PortOwnerProbeReport.Empty("COM7", PortOwnerProbeStatus.Found), PortOwnerProbeProtocol.JsonOptions);
        Assert.Throws<InvalidDataException>(() => WindowsPortOwnerProbeClient.ParseReply(json, "COM7", "COM7"));
    }
}
