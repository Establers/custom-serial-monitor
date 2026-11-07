using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialDirectInputOrderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADriverChangeCannotOvertakePreviouslyQueuedKeys(bool configure)
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        var writeGate = (SemaphoreSlim)typeof(SerialService).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        var observed = new ConcurrentQueue<(string Text, SerialControlLineMode Dtr, int Baud)>();
        serial.CaptureBytesReceived += (_, chunk) =>
        {
            var text = Encoding.UTF8.GetString(chunk.Bytes);
            if (text is "A" or "B" or "C") observed.Enqueue((text, serial.ControlLineStatus.Dtr, serial.AppliedBaudRate));
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var input = new TerminalInputSession(async (text, bytes, token) =>
        {
            if (text == "A") started.TrySetResult();
            await serial.SendTerminalInputAsync(bytes, generation, token);
            if (text == "C") finished.TrySetResult();
        }, message => finished.TrySetException(new IOException(message)));
        await writeGate.WaitAsync();
        try
        {
            Assert.True(input.TryEnqueue("A"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(input.TryEnqueue("B"));
            Task change = configure
                ? input.RunOrderedAsync(ct => serial.ConfigureAsync(serial.AppliedConfiguration! with { BaudRate = 57600 }, generation, ct), default)
                : input.RunOrderedAsync(ct => serial.SetControlLineAsync(SerialControlLine.Dtr, true, generation, ct), default);
            Assert.True(input.TryEnqueue("C"));
            writeGate.Release();
            await change.WaitAsync(TimeSpan.FromSeconds(3));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { if (writeGate.CurrentCount == 0) writeGate.Release(); }
        Assert.Equal(new[]
        {
            ("A", SerialControlLineMode.Off, 115200), ("B", SerialControlLineMode.Off, 115200),
            ("C", configure ? SerialControlLineMode.Off : SerialControlLineMode.On, configure ? 57600 : 115200)
        }, observed.ToArray());
        Assert.Equal(generation, serial.ReceiveSessionGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingOrStoppingBeforeAQueuedChangeStartsNeverExecutesIt(bool stopSession)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<string>();
        await using var input = new TerminalInputSession(async (text, _, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            sent.Enqueue(text);
        }, _ => { });
        Assert.True(input.TryEnqueue("before"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancel = new CancellationTokenSource();
        var changed = false;
        var operation = input.RunOrderedAsync(_ => { changed = true; return Task.FromResult(true); }, cancel.Token);
        if (stopSession) input.Stop(); else cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(changed);
        release.TrySetResult();
        if (!stopSession)
        {
            Assert.True(input.TryEnqueue("after"));
            await input.RunOrderedAsync(_ => Task.FromResult(true), default).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new[] { "before", "after" }, sent.ToArray());
        }
    }
}
