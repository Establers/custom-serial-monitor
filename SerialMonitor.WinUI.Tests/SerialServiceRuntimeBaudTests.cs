using System.Reflection;
using System.Text;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialServiceRuntimeBaudTests
{
    [Fact]
    public async Task MockRuntimeBaudChangeKeepsSameSessionChannelAndTxCounters()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new SerialSettings { PortName = "MOCK", BaudRate = 115200 }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        var channel = serial.ReceivedBytes;
        await serial.SendBytesAsync([0x01, 0x02], "before change", default);
        var written = serial.WrittenByteCount;
        Assert.Equal(115200, serial.AppliedBaudRate);

        var applied = await serial.ChangeBaudRateAsync(250000, default);

        Assert.Equal(250000, applied);
        Assert.Equal(250000, serial.AppliedBaudRate);
        Assert.True(serial.IsConnected);
        Assert.Equal(generation, serial.ReceiveSessionGeneration);
        Assert.Same(channel, serial.ReceivedBytes);
        Assert.Equal(written, serial.WrittenByteCount);
        await serial.SendBytesAsync([0x03, 0x04], "after change", default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        string received;
        do { received = Encoding.UTF8.GetString((await channel.ReadAsync(timeout.Token)).Bytes); }
        while (received != "after change" + Environment.NewLine);
        Assert.Equal(written + 2, serial.WrittenByteCount);
        Assert.Equal("after change" + Environment.NewLine, received);
    }

    [Fact]
    public async Task CancellationWhileWaitingForExistingWriteLeavesBaudAndSessionUntouched()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new SerialSettings { PortName = "MOCK", BaudRate = 115200 }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        var writeGate = (SemaphoreSlim)typeof(SerialService).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        await writeGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var configuring = serial.ChangeBaudRateAsync(57600, cancellation.Token);
            Assert.False(configuring.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configuring.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(115200, serial.AppliedBaudRate);
            Assert.Equal(generation, serial.ReceiveSessionGeneration);
            Assert.True(serial.IsConnected);
        }
        finally { writeGate.Release(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000_001)]
    public async Task InvalidBaudLeavesCurrentConnectionUntouched(int invalidBaud)
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new SerialSettings { PortName = "MOCK", BaudRate = 115200 }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => serial.ChangeBaudRateAsync(invalidBaud, default));
        Assert.Equal(115200, serial.AppliedBaudRate);
        Assert.Equal(generation, serial.ReceiveSessionGeneration);
        Assert.True(serial.IsConnected);
    }
}
