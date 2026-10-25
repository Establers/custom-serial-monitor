using System.Text;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class AutomationRawRxTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureObserverReceivesActualMockEchoWithoutConsumingParserChannel(bool bridgePriority)
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new(), default);
        serial.SetRawBridgePriorityEnabled(bridgePriority);
        var observed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        serial.CaptureBytesReceived += (generation, chunk) =>
        {
            if (Encoding.UTF8.GetString(chunk.Bytes) == "abc1d234" + Environment.NewLine) observed.TrySetResult(generation);
        };
        await serial.SendBytesAsync("command"u8.ToArray(), "abc1d234", default);
        Assert.Equal(serial.ReceiveSessionGeneration, await observed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ReceivedByteChunk received;
        do { received = await serial.ReceivedBytes.ReadAsync(timeout.Token); }
        while (Encoding.UTF8.GetString(received.Bytes) != "abc1d234" + Environment.NewLine);
        Assert.Equal("abc1d234" + Environment.NewLine, Encoding.UTF8.GetString(received.Bytes));
    }
}
