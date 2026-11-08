using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TerminalTransmitHistoryTests
{
    [Fact]
    public void SuccessfulKeysAreCoalescedAndRetainedIncludingUnsubmittedInputOnOff()
    {
        var retained = new List<LogLine>();
        var history = new TerminalTransmitHistory(retained.Add);
        foreach (var key in new[] { "S", "T", "O", "P", "\r", "\n", "ID?" }) history.Sent(key, DateTimeOffset.Now);
        Assert.True(history.MayHavePendingCommand);
        history.Flush();
        var log = new LogViewModel(100);
        log.AddRange(retained);
        var content = log.GetVisibleSearchContentSnapshot();
        Assert.Equal(new[] { "STOP", "ID?" }, content.Select(line => line.PayloadText));
        Assert.All(content, line => Assert.Equal(LogDirection.Tx, line.Direction));
        Assert.Contains("ID?", log.GetVisibleTextSnapshot());
        history.Flush();
        Assert.Equal(2, retained.Count);
    }

    [Fact]
    public void EditingBytesAreSafeVisibleEvidenceAndDoNotImpersonateACompletedRemoteCommand()
    {
        var retained = new List<LogLine>();
        var history = new TerminalTransmitHistory(retained.Add);
        history.Sent("STX\x7fOP\t\x1b[A\r", DateTimeOffset.Now);
        var record = Assert.Single(retained);
        Assert.Equal(@"STX\x7FOP\t\x1B[A", record.Text);
        Assert.Equal(Encoding.UTF8.GetBytes("STX\x7fOP\t\x1b[A\r"), record.RawBytes);
        Assert.False(history.MayHavePendingCommand);
    }

    [Fact]
    public void FlushIsBoundedAtUnicodeBoundariesAndDoesNotForgetAnUnfinishedCommand()
    {
        var retained = new List<LogLine>();
        var history = new TerminalTransmitHistory(retained.Add);
        var text = new string('x', TerminalTransmitHistory.MaxRecordCharacters - 1) + "😀tail";
        history.Sent(text, DateTimeOffset.Now);
        history.Flush();
        Assert.Equal(2, retained.Count);
        Assert.All(retained, line => Assert.InRange(Encoding.UTF8.GetString(line.RawBytes!).Length, 1, TerminalTransmitHistory.MaxRecordCharacters));
        Assert.Equal(text, string.Concat(retained.Select(line => Encoding.UTF8.GetString(line.RawBytes!))));
        Assert.True(history.MayHavePendingCommand);
        history.Sent("\x03", DateTimeOffset.Now);
        Assert.False(history.MayHavePendingCommand);
        history.Sent("\t", DateTimeOffset.Now);
        Assert.True(history.MayHavePendingCommand);
        history.Sent("\r", DateTimeOffset.Now);
        Assert.False(history.MayHavePendingCommand);
    }

    [Fact]
    public async Task SequenceLockDrainsAcceptedInputRejectsNewKeysAndResumesTheSameSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new List<string>();
        var retained = new List<LogLine>();
        var history = new TerminalTransmitHistory(retained.Add);
        await using var session = new TerminalInputSession(async (text, _, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            wire.Add(text); history.Sent(text, DateTimeOffset.Now);
        }, message => throw new InvalidOperationException(message));
        Assert.True(session.TryEnqueue("STOP"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.TryEnqueue("\r"));
        session.SetInputLocked(true);
        Assert.False(session.TryEnqueue("must not send\r"));
        var barrier = session.RunOrderedAsync(_ =>
        {
            Assert.False(history.MayHavePendingCommand);
            wire.Add("SEQUENCE\r");
            return Task.FromResult(true);
        }, default);
        Assert.False(barrier.IsCompleted);
        release.TrySetResult();
        await barrier.WaitAsync(TimeSpan.FromSeconds(3));
        session.SetInputLocked(false);
        Assert.True(session.TryEnqueue("PING\r"));
        await session.RunOrderedAsync(_ => Task.FromResult(true), default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "STOP", "\r", "SEQUENCE\r", "PING\r" }, wire);
        Assert.Equal(new[] { "STOP", "PING" }, retained.Select(line => line.Text));
    }
}
