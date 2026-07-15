using System.Text;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialServiceMockEchoTests
{
    [Fact]
    public async Task SendBytesAsync_MockResponseContainsOnlyDisplayPayload()
    {
        await using var service = new SerialService();
        await service.ConnectAsync(
            new SerialSettings { PortName = "MOCK" },
            new SerialReceiveOptions(),
            CancellationToken.None);

        while (service.ReceivedBytes.TryRead(out _))
        {
        }

        await service.SendBytesAsync(
            new byte[] { 0x12, 0x34 },
            "12 34",
            CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        ReceivedByteChunk response;
        do
        {
            response = await service.ReceivedBytes.ReadAsync(timeout.Token);
        }
        while (Encoding.UTF8.GetString(response.Bytes) != $"12 34{Environment.NewLine}");

        var text = Encoding.UTF8.GetString(response.Bytes);
        Assert.Equal($"12 34{Environment.NewLine}", text);
        Assert.DoesNotContain("mock device received", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[HEX]", text, StringComparison.OrdinalIgnoreCase);
    }
}
