using System.Text;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialReceiveSessionTests
{
    [Fact]
    public async Task ReconnectUsesNewChannelAndGeneration()
    {
        await using var service = new SerialService();
        var settings = new SerialSettings { PortName = "MOCK" };

        await service.ConnectAsync(settings, new SerialReceiveOptions(), CancellationToken.None);
        var firstReader = service.ReceivedBytes;
        var firstGeneration = service.ReceiveSessionGeneration;

        await service.DisconnectAsync(CancellationToken.None);
        Assert.True(firstReader.Completion.IsCompleted);

        await service.ConnectAsync(settings, new SerialReceiveOptions(), CancellationToken.None);
        var secondReader = service.ReceivedBytes;

        Assert.NotSame(firstReader, secondReader);
        Assert.True(service.ReceiveSessionGeneration > firstGeneration);

        await service.SendMockCrlfAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        ReceivedByteChunk chunk;
        do
        {
            chunk = await secondReader.ReadAsync(timeout.Token);
        }
        while (!Encoding.UTF8.GetString(chunk.Bytes).Equals("\r\n", StringComparison.Ordinal));

        Assert.Equal("\r\n", Encoding.UTF8.GetString(chunk.Bytes));
    }
}
