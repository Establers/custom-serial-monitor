using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialReceiveSessionTests
{
    [Fact]
    public async Task CanceledPipelineStop_DoesNotLetOldWorkerCloseReconnectedLogs()
    {
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        var oldSource = new DelayedCompletionReader();
        var nextSource = Channel.CreateUnbounded<ReceivedByteChunk>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stopCancellation = new CancellationTokenSource();
        Task? restart = null;
        try
        {
            await pipeline.StartAsync(oldSource, new SerialSettings(), timeout.Token);
            await oldSource.WaitStarted.Task.WaitAsync(timeout.Token);
            var oldLogs = pipeline.Logs;
            var stop = pipeline.StopAsync(stopCancellation.Token);
            stopCancellation.Cancel();
            var stopError = await Record.ExceptionAsync(() => stop);

            restart = pipeline.StartAsync(nextSource.Reader, new SerialSettings(), timeout.Token);
            Assert.False(restart.IsCompleted, "Restart must join the previous worker before replacing its channel.");
            Assert.Same(oldLogs, pipeline.Logs);
            Assert.IsAssignableFrom<OperationCanceledException>(stopError);

            oldSource.Release.TrySetResult(false);
            await restart.WaitAsync(timeout.Token);
            await nextSource.Writer.WriteAsync(ReceivedByteChunk.Capture("new session\r\n"u8.ToArray()), timeout.Token);
            Assert.Equal("new session", (await pipeline.Logs.ReadAsync(timeout.Token)).Text);
        }
        finally
        {
            oldSource.Release.TrySetResult(false);
            if (restart is not null)
            {
                await restart.WaitAsync(timeout.Token);
            }
            await pipeline.StopAsync(CancellationToken.None);
        }
    }

    private sealed class DelayedCompletionReader : ChannelReader<ReceivedByteChunk>
    {
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool TryRead(out ReceivedByteChunk item)
        {
            item = null!;
            return false;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            // Hold the old worker at an async boundary even after cancellation,
            // as can happen while its continuation is waiting to be scheduled.
            WaitStarted.TrySetResult();
            return new ValueTask<bool>(Release.Task);
        }
    }

    [Fact]
    public async Task RepeatedReconnect_ImmediateTxEchoReachesVisibleLog()
    {
        await using var service = new SerialService();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        var log = new LogViewModel(capacity: 100);
        var settings = new SerialSettings { PortName = "MOCK" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        for (var attempt = 0; attempt < 25; attempt++)
        {
            try
            {
                await service.ConnectAsync(settings, new SerialReceiveOptions(), timeout.Token);
                await pipeline.StartAsync(service.ReceivedBytes, settings, timeout.Token);
                var command = $"reconnect-{attempt}";
                await service.SendBytesAsync(Encoding.UTF8.GetBytes(command), command, timeout.Token);
                LogLine response;
                do
                {
                    response = await pipeline.Logs.ReadAsync(timeout.Token);
                }
                while (response.Text != command);

                log.AddRange([LogLine.Tx(command), response]);
                Assert.Contains(command, log.GetVisibleTextSnapshot(), StringComparison.Ordinal);
                log.Clear();
            }
            finally
            {
                await pipeline.StopAsync(CancellationToken.None);
                await service.DisconnectAsync(CancellationToken.None);
            }
        }
    }

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
