using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using SerialMonitor.Core.Diagnostics;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

[CollectionDefinition("PortOwnerProbeProcesses", DisableParallelization = true)]
public sealed class PortOwnerProbeProcessCollection { }

[Collection("PortOwnerProbeProcesses")]
public sealed class PortOwnerProbeProcessTests
{
    [Fact]
    public async Task DeployedWorker_RunsWithoutOpeningAnAbsentPort()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "PortDiagnostics");
        foreach (var file in new[] { "SerialMonitor.PortDiagnosticWorker.exe", "SerialMonitor.PortDiagnosticWorker.dll", "SerialMonitor.PortDiagnosticWorker.deps.json", "SerialMonitor.PortDiagnosticWorker.runtimeconfig.json", "SerialMonitor.Core.dll" })
            Assert.True(File.Exists(Path.Combine(directory, file)), file);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var report = await new WindowsPortOwnerProbeClient().FindAsync("COM99999", stop.Token);
        Assert.Equal(PortOwnerProbeStatus.PortNotPresent, report.Status);
        Assert.Empty(report.Owners);
    }

    [Fact]
    public async Task MissingWorker_IsAnExplicitUnavailableResult()
    {
        var client = new WindowsPortOwnerProbeClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), TimeSpan.FromSeconds(1));
        Assert.Equal(PortOwnerProbeStatus.Unavailable, (await client.FindAsync("COM7", default)).Status);
    }

    [Fact]
    public async Task StartupFailure_ReleasesTheGateForTheNextLookup()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "PortDiagnostics", "SerialMonitor.PortDiagnosticWorker.exe");
        var client = new WindowsPortOwnerProbeClient(worker, TimeSpan.FromSeconds(2), () => throw new IOException("fixture startup failure"));
        Assert.Equal(PortOwnerProbeStatus.Unavailable, (await client.FindAsync("COM7", default)).Status);
        await WaitForProbeGateAsync();
    }

    [Fact]
    public async Task LateStartup_IsBoundedAndKeepsTheGateUntilItRetires()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "PortDiagnostics", "SerialMonitor.PortDiagnosticWorker.exe");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var client = new WindowsPortOwnerProbeClient(worker, TimeSpan.FromMilliseconds(500), () =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture release missing");
            return WindowsPortOwnerProbeClient.CreateStartInfo(worker, "COM99999");
        });
        try
        {
            var first = client.FindAsync("COM7", default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(PortOwnerProbeStatus.TimedOut, (await first.WaitAsync(TimeSpan.FromSeconds(2))).Status);
            Assert.Equal(PortOwnerProbeStatus.Unavailable, (await new WindowsPortOwnerProbeClient().FindAsync("COM99999", default)).Status);
        }
        finally { release.Set(); await WaitForProbeGateAsync(); }
    }

    [Fact]
    public async Task TimeoutKillsOnlyTheOwnedFixture_AndReleasesItsFileLock()
    {
        var fixture = CreateFixture(TimeSpan.FromMilliseconds(400));
        try
        {
            var watch = Stopwatch.StartNew();
            var result = await fixture.Client.FindAsync("COM7", default);
            Assert.Equal(PortOwnerProbeStatus.TimedOut, result.Status);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
            await WaitForProbeGateAsync();
            await WaitForUnlockedAsync(fixture.LockPath);
        }
        finally { await WaitForProbeGateAsync(); if (File.Exists(fixture.LockPath)) File.Delete(fixture.LockPath); }
    }

    [Fact]
    public async Task CancellationAndRepeatedRequests_DoNotAccumulateWorkers()
    {
        var fixture = CreateFixture(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        try
        {
            var first = fixture.Client.FindAsync("COM7", stop.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(fixture.LockPath)) await Task.Delay(10, deadline.Token);
            var second = await fixture.Client.FindAsync("COM7", deadline.Token);
            Assert.Equal(PortOwnerProbeStatus.Unavailable, second.Status);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await WaitForUnlockedAsync(fixture.LockPath);
        }
        finally { stop.Cancel(); await WaitForProbeGateAsync(); await WaitForUnlockedAsync(fixture.LockPath); if (File.Exists(fixture.LockPath)) File.Delete(fixture.LockPath); }
    }

    [Com0comPair0Fact]
    public async Task ActualExclusiveVirtualComOwner_IsIdentifiedWithoutChangingTheConnection()
    {
        VerifyVirtualPort();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var owner = new SerialService(); await using var denied = new SerialService();
        await owner.ConnectAsync(new() { PortName = "CNCA0" }, new(), timeout.Token);
        var generation = owner.ReceiveSessionGeneration;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => denied.ConnectAsync(new() { PortName = "CNCA0" }, new(), timeout.Token));
        var report = await new SerialConnectFailureDiagnostics().DiagnoseAsync("CNCA0", error, timeout.Token);
        Assert.True(report.OwnerProbe.Status == PortOwnerProbeStatus.Found,
            $"Owner lookup returned {report.OwnerProbe.Status}: {report.OwnerProbe.Detail}");
        var process = Assert.Single(report.OwnerProbe.Owners); Assert.Equal(Environment.ProcessId, process.ProcessId); Assert.NotEmpty(process.ProcessName);
        Assert.True(owner.IsConnected); Assert.Equal(generation, owner.ReceiveSessionGeneration);
        Assert.Equal(0, owner.ReceivedByteCount); Assert.Equal(0, owner.WrittenByteCount);
    }

    [Com0comPair0Fact]
    public async Task ActualOwnerInAnotherProcess_IsFoundWithoutClosingThatProcess()
    {
        VerifyVirtualPort();
        const string script = """
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            using Microsoft.Win32.SafeHandles;
            public static class VirtualComOwnerFixture {
                [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
                static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
                public static SafeFileHandle Open() { return CreateFile(@"\\.\CNCA0",0xC0000000,0,IntPtr.Zero,3,0,IntPtr.Zero); }
            }
            '@
            $port = [VirtualComOwnerFixture]::Open()
            if ($port.IsInvalid) { throw 'Virtual COM fixture could not open CNCA0.' }
            try { [Console]::Out.WriteLine('READY'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null }
            finally { $port.Dispose() }
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        try
        {
            Assert.Equal("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var denied = new SerialService();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => denied.ConnectAsync(new() { PortName = "CNCA0" }, new(), timeout.Token));
            var report = await new SerialConnectFailureDiagnostics().DiagnoseAsync("CNCA0", error, timeout.Token);
            Assert.True(report.OwnerProbe.Status == PortOwnerProbeStatus.Found,
                $"Owner lookup returned {report.OwnerProbe.Status}: {report.OwnerProbe.Detail}");
            var owner = Assert.Single(report.OwnerProbe.Owners);
            Assert.Equal(process.Id, owner.ProcessId); Assert.Equal("powershell.exe", owner.ProcessName, ignoreCase: true);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
    }

    private static void VerifyVirtualPort()
    {
        using var registry = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\com0com\port\CNCA0");
        Assert.NotNull(registry); Assert.Equal("com0com", registry.GetValue("Service"));
        using var parameters = registry.OpenSubKey("Device Parameters"); Assert.Equal("CNCA0", parameters!.GetValue("PortName"));
    }

    private static (WindowsPortOwnerProbeClient Client, string LockPath) CreateFixture(TimeSpan timeout)
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "UpdateTestWorker", "SerialMonitor.UpdateTestWorker.dll");
        Assert.True(File.Exists(worker));
        var path = Path.Combine(Path.GetTempPath(), "serial-port-owner-test-" + Guid.NewGuid().ToString("N"));
        var client = new WindowsPortOwnerProbeClient(worker, timeout, () =>
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(worker); start.ArgumentList.Add(path); start.ArgumentList.Add("hold");
            return start;
        });
        return (client, path + ".lock");
    }

    private static async Task WaitForUnlockedAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (File.Exists(path))
        {
            try { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return; }
            catch (IOException) { await Task.Delay(10, timeout.Token); }
        }
    }

    private static async Task WaitForProbeGateAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var result = await new WindowsPortOwnerProbeClient().FindAsync("COM99999", timeout.Token);
            if (result.Status == PortOwnerProbeStatus.PortNotPresent) return;
            Assert.Equal(PortOwnerProbeStatus.Unavailable, result.Status);
            await Task.Delay(10, timeout.Token);
        }
    }
}
