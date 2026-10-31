using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

// Interactive, receive-only hardware check. Never run against an implicitly
// selected port: both the port and its exact USB PnP instance must be supplied.
public sealed class PhysicalUsbDisconnectTests(ITestOutputHelper output)
{
    [PhysicalUsbFact]
    public async Task UsbRemovalDuringResponseObservationStopsAndDisposesWithoutCrashing()
    {
        var port = Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_USB_PORT")!;
        var deviceId = Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_USB_DEVICE")!;
        var resultFile = Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_USB_RESULT")!;
        Assert.StartsWith("USB\\", deviceId, StringComparison.OrdinalIgnoreCase);
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + deviceId + @"\Device Parameters"))
            Assert.Equal(port, key?.GetValue("PortName"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var sequenceStop = new CancellationTokenSource();
        var root = Path.Combine(Path.GetTempPath(), nameof(PhysicalUsbDisconnectTests), Guid.NewGuid().ToString("N"));
        var serial = new SerialService();
        var stage = "starting";
        double? stopMs = null, disconnectMs = null, disposeMs = null;
        string? fault = null;
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store, new("USB removal receive-only", 1, port, "Utf8",
                [new(1, "", "Terminal", "None", 600000, new("text", "USB_TEST_RESPONSE_NEVER_TRANSMITTED"), "", "RxCheck")]), Encoding.UTF8, deadline.Token);
            serial.CaptureBytesReceived += (generation, chunk) => session.Offer(generation, chunk.Bytes, chunk.ReceivedTimestamp);
            serial.StatusChanged += (_, _) => session.ConnectionChanged(serial.ReceiveSessionGeneration, serial.IsConnected);
            await serial.ConnectAsync(new()
            {
                PortName = port, BaudRate = 115200, DataBits = 8,
                Parity = SerialParityMode.None, StopBits = SerialStopBitsMode.One,
                Handshake = SerialHandshakeMode.None, DtrEnable = false, RtsEnable = false
            }, new(), deadline.Token);
            var generation = serial.ReceiveSessionGeneration;
            await session.BeginAsync(new(1, "", "Terminal", "None", 600000,
                new("text", "USB_TEST_RESPONSE_NEVER_TRANSMITTED"), "", "RxCheck"), 1, 1, generation, sequenceStop.Token);
            var checking = session.CheckFullWindowAsync(sequenceStop.Token);
            stage = "ready_to_unplug";
            await Save();
            // Drain normally, so full RX queues cannot hide a driver failure.
            var drain = Task.Run(async () =>
            {
                await foreach (var _ in serial.ReceivedBytes.ReadAllAsync(deadline.Token)) { }
            });
            while (serial.IsConnected) await Task.Delay(20, deadline.Token);
            stage = "device_fault_observed";
            fault = serial.LastError;
            Assert.Equal(SerialConnectionState.Faulted, serial.ConnectionState);
            Assert.Contains("Serial receive failed", fault);
            var timer = Stopwatch.StartNew();
            sequenceStop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking.WaitAsync(TimeSpan.FromSeconds(2)));
            await session.FinishAsync("canceled").WaitAsync(TimeSpan.FromSeconds(2));
            stopMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal("inconclusive", Assert.Single((await store.ReadPageAsync(session.RunId)).Rows).CheckStatus);
            timer.Restart();
            using (var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                await serial.DisconnectAsync(stopDeadline.Token).WaitAsync(TimeSpan.FromSeconds(3));
            disconnectMs = timer.Elapsed.TotalMilliseconds;
            await drain.WaitAsync(TimeSpan.FromSeconds(2));
            timer.Restart();
            await serial.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            disposeMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(0, serial.WrittenByteCount);
            stage = "passed";
            await Save();
            output.WriteLine(await File.ReadAllTextAsync(resultFile));
        }
        catch (Exception ex)
        {
            stage = "failed: " + stage;
            fault = ex.ToString();
            await Save();
            throw;
        }
        finally
        {
            sequenceStop.Cancel();
            await serial.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        Task Save() => File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new
        { stage, port, baudRate = 115200, dataBits = 8, parity = "None", stopBits = "One", handshake = "None",
            txBytes = serial.WrittenByteCount, rxBytes = serial.ReceivedByteCount, stopMs, disconnectMs, disposeMs, fault, at = DateTimeOffset.Now }));
    }
}

public sealed class PhysicalUsbFactAttribute : FactAttribute
{
    public PhysicalUsbFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_USB_PORT")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_USB_DEVICE")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_USB_RESULT")))
            Skip = "Interactive USB unplug check: supply explicit USB port, PnP instance and result path.";
    }
}
