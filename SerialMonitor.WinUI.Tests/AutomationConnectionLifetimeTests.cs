using System.Text;
using System.Reflection;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class AutomationConnectionLifetimeTests
{
    [Fact]
    public async Task RequestCancellationDuringPreviousTeardownCannotStartNewSession()
    {
        await using var serial = new SerialService();
        using var request = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(SerialService).GetField("_pendingReceiveTeardown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(serial, retirement.Task);
        try
        {
            using var startup = new ConnectionStartupCancellation(request.Token, lifetime);
            var opening = serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new(), lifetime.Token);
            Assert.False(opening.IsCompleted);
            request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(serial.IsConnected);
            Assert.Equal(0, serial.ReceiveSessionGeneration);
        }
        finally { retirement.TrySetResult(); }
    }

    [Fact]
    public async Task RequestCancellationWhileWaitingForLifecycleGateCannotStartNewSession()
    {
        await using var serial = new SerialService();
        using var request = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        var lifecycle = (SemaphoreSlim)typeof(SerialService).GetField("_lifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(serial)!;
        await lifecycle.WaitAsync();
        try
        {
            using var startup = new ConnectionStartupCancellation(request.Token, lifetime);
            var opening = serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new(), lifetime.Token);
            request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(serial.IsConnected);
            Assert.Equal(0, serial.ReceiveSessionGeneration);
        }
        finally { lifecycle.Release(); }
    }

    [Fact]
    public async Task ClosingSuccessfulRequestDoesNotDisconnectEstablishedMockPort()
    {
        await using var serial = new SerialService();
        using var request = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        using (new ConnectionStartupCancellation(request.Token, lifetime))
            await serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new(), lifetime.Token);

        request.Cancel(); // Pipe host cancels each completed request's lifetime.
        Assert.True(serial.IsConnected);
        Assert.False(lifetime.IsCancellationRequested);
        await serial.SendBytesAsync("status"u8.ToArray(), "still connected", default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        string text;
        do { text = Encoding.UTF8.GetString((await serial.ReceivedBytes.ReadAsync(timeout.Token)).Bytes); }
        while (text != "still connected" + Environment.NewLine);
        Assert.Equal("still connected" + Environment.NewLine, text);
    }
}
