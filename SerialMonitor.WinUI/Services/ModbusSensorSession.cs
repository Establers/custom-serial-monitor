using System.Threading.Channels;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

// One bounded inbox, one engine consumer. Snapshots replace each other instead
// of queuing UI notifications. A session is never reused after stop/reconnect.
public sealed class ModbusSensorSession
{
    internal abstract record Work;
    internal sealed record Receive(ReceivedByteChunk Chunk) : Work;
    internal sealed record Edit(int? Index, string? Value, CancellationToken Token, TaskCompletionSource Completion) : Work;
    internal Channel<Work> Inbox { get; } = Channel.CreateBounded<Work>(new BoundedChannelOptions(64)
    { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private int _state;
    private ModbusSensorSnapshot? _snapshot;
    public bool IsRunning => Volatile.Read(ref _state) == 1;
    public ModbusSensorSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    internal void Start(ModbusSensorSnapshot snapshot)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) throw new InvalidOperationException("이미 종료된 시뮬레이터 실행이에요.");
        Publish(snapshot);
    }
    internal void Publish(ModbusSensorSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
    public Task SetValueAsync(int index, string value, CancellationToken token = default) => Enqueue(index, value, token);
    public Task ResetAsync(int? index = null, CancellationToken token = default) => Enqueue(index, null, token);
    private async Task Enqueue(int? index, string? value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsRunning) throw new InvalidOperationException("응답 시작 후 값을 변경하세요.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Inbox.Writer.TryWrite(new Edit(index, value, token, completion)))
            throw new InvalidOperationException("처리 중인 요청이 많아요. 잠시 후 다시 시도하세요.");
        await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }
    internal void Complete()
    {
        Interlocked.Exchange(ref _state, 2);
        Inbox.Writer.TryComplete();
        while (Inbox.Reader.TryRead(out var work))
            if (work is Edit edit) edit.Completion.TrySetCanceled();
    }
}
