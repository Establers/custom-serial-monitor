using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class TerminalInputSessionTests
{
    [Fact]
    public async Task KeysAndPasteKeepTheirOrderAndExactBytesWithoutAddingLineEndings()
    {
        var written = new ConcurrentQueue<byte[]>();
        var errors = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new TerminalInputSession(async (_, bytes, token) =>
        {
            await release.Task.WaitAsync(token);
            written.Enqueue(bytes);
            if (written.Count == 4) complete.TrySetResult();
        }, errors.Enqueue);

        var inputs = new[] { "cd ab/c", "\t", "\u001b[A\u007f\u0003", "한글 😀\r" };
        foreach (var input in inputs) Assert.True(session.TryEnqueue(input));
        release.SetResult();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Encoding.UTF8.GetBytes(string.Concat(inputs)), written.SelectMany(bytes => bytes).ToArray());
        Assert.Empty(errors);
    }

    [Fact]
    public async Task OffCancelsTheActiveWriteAndDiscardsQueuedKeys()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        var session = new TerminalInputSession(async (text, _, token) =>
        {
            attempted.Enqueue(text);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, errors.Enqueue);
        Assert.True(session.TryEnqueue("first"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.TryEnqueue("queued\r"));
        session.Stop();
        Assert.False(session.TryEnqueue("after OFF\r"));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "first" }, attempted.ToArray());
        Assert.Empty(errors);
    }

    [Fact]
    public async Task FullQueueStopsInsteadOfSilentlyDroppingPartOfACommand()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<string>();
        await using var session = new TerminalInputSession(async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, errors.Enqueue);
        Assert.True(session.TryEnqueue("blocked"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var accepted = 0;
        while (session.TryEnqueue("next"))
        {
            Assert.True(++accepted < 100, "Pending input must be bounded.");
        }
        Assert.InRange(accepted, 1, 32);
        Assert.Contains("queue is full", Assert.Single(errors));
        Assert.False(session.TryEnqueue("\r"));
    }

    [Fact]
    public async Task OversizedPasteAndInvalidUnicodeAreRejectedBeforeSending()
    {
        foreach (var text in new[] { new string('a', TerminalInputSession.MaxInputCharacters + 1), "\ud800" })
        {
            var errors = new ConcurrentQueue<string>();
            await using var session = new TerminalInputSession((_, _, _) =>
                throw new InvalidOperationException("Must not write invalid input."), errors.Enqueue);
            Assert.False(session.TryEnqueue(text));
            Assert.Single(errors);
            Assert.False(session.TryEnqueue("\r"));
        }
    }

    [Fact]
    public async Task WriteFailureIsReportedAndStopsTheSession()
    {
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new TerminalInputSession((_, _, _) =>
            throw new IOException("device removed"), message => failed.TrySetResult(message));
        Assert.True(session.TryEnqueue("a"));
        Assert.Contains("device removed", await failed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(session.TryEnqueue("\r"));
    }

    [Fact]
    public async Task DirectInputUsesTheSerialWriteGateAndCannotCrossAReconnect()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new(), default);
        var oldGeneration = serial.ReceiveSessionGeneration;
        var gate = (SemaphoreSlim)typeof(SerialService).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        await gate.WaitAsync();
        var oldWrite = serial.SendTerminalInputAsync("old\t\r"u8.ToArray(), oldGeneration, default);
        Assert.False(oldWrite.IsCompleted);
        var disconnect = serial.DisconnectAsync(default);
        gate.Release();
        // Either the old session completes before disconnect or it is fenced;
        // it can never be retried into the next connection.
        try { await oldWrite; } catch (InvalidOperationException) { }
        await disconnect;
        await serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new(), default);
        var before = serial.WrittenByteCount;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            serial.SendTerminalInputAsync("stale\r"u8.ToArray(), oldGeneration, default));
        Assert.Equal(before, serial.WrittenByteCount);
        await serial.SendTerminalInputAsync("new\t\r"u8.ToArray(), serial.ReceiveSessionGeneration, default);
        Assert.Equal(before + 5, serial.WrittenByteCount);
    }

    [Fact]
    public async Task MockEchoKeepsDirectInputControlBytesWithoutAnExtraNewline()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new(), default);
        var bytes = "cd ab/c\t\u001b[A\u007f\u0003\r"u8.ToArray();
        await serial.SendTerminalInputAsync(bytes, serial.ReceiveSessionGeneration, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ReceivedByteChunk chunk;
        do { chunk = await serial.ReceivedBytes.ReadAsync(timeout.Token); }
        while (!chunk.Bytes.AsSpan().StartsWith("cd ab/c"u8));
        Assert.Equal(bytes, chunk.Bytes);
    }
}
