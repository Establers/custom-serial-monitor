using System.Diagnostics;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class ModbusSensorService(ISerialService serial, Action<byte[], long> transmitted)
{
    public const string Owner = "Modbus sensor simulator";
    public async Task RunAsync(ModbusSensorDefinition definition, Action responded, CancellationToken token, ModbusSensorSession? session = null)
    {
        var engine = new ModbusSensorEngine(definition);
        await using var lease = serial.AcquireExclusiveSession(Owner);
        session ??= new ModbusSensorSession();
        session.Start(engine.GetSnapshot());
        var queue = session.Inbox;
        int queuedBytes = 0, overflow = 0;
        long latestReceive = 0;
        var timing = new ModbusResponseTiming(serial.AppliedBaudRate, serial.AppliedConfiguration);
        void Receive(long generation, ReceivedByteChunk chunk)
        {
            if (generation != lease.Generation || chunk.Bytes.Length == 0) return;
            Interlocked.Exchange(ref latestReceive, Stopwatch.GetTimestamp());
            if (Interlocked.Add(ref queuedBytes, chunk.Bytes.Length) > 65536 || !queue.Writer.TryWrite(new ModbusSensorSession.Receive(chunk)))
            { Interlocked.Exchange(ref overflow, 1); queue.Writer.TryComplete(new IOException("시뮬레이터 수신 대기열이 가득 찼어요. 응답을 중지했어요.")); }
        }
        void Changed(object? sender, EventArgs args)
        {
            if (!serial.IsConnected || serial.ReceiveSessionGeneration != lease.Generation)
                queue.Writer.TryComplete(new IOException("연결이 바뀌었어요. 장치 응답을 다시 시작하세요."));
        }
        serial.CaptureBytesReceived += Receive;
        serial.StatusChanged += Changed;
        try
        {
            Changed(null, EventArgs.Empty);
            var framer = new ModbusRequestFramer();
            long lastChunk = 0, lastWriteStarted = 0, lastWriteCompleted = 0;
            byte[]? previousResponse = null;
            async Task WaitForResponseSlot()
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref overflow) != 0) throw new IOException("시뮬레이터 수신 대기열 초과로 응답을 중지했어요.");
                    if (!serial.IsConnected || serial.ReceiveSessionGeneration != lease.Generation) throw new IOException("연결이 바뀌었어요. 장치 응답을 다시 시작하세요.");
                    var remaining = timing.FrameGap - Stopwatch.GetElapsedTime(Interlocked.Read(ref latestReceive));
                    if (lastWriteCompleted != 0)
                    {
                        var afterWrite = timing.WireDuration(previousResponse!.Length) + timing.FrameGap - Stopwatch.GetElapsedTime(lastWriteCompleted);
                        if (afterWrite > remaining) remaining = afterWrite;
                    }
                    if (remaining <= TimeSpan.Zero) return;
                    // Round up and yield; never spin or block the UI for sub-millisecond gaps.
                    await Task.Delay((int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, 50), token).ConfigureAwait(false);
                }
            }
            await foreach (var work in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (work is ModbusSensorSession.Edit edit)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        edit.Token.ThrowIfCancellationRequested();
                        if (!serial.IsConnected || serial.ReceiveSessionGeneration != lease.Generation || Volatile.Read(ref overflow) != 0)
                            throw new IOException("통신이 중지됐어요. 응답 시작 후 다시 변경하세요.");
                        if (edit.Value is null) engine.ResetValues(edit.Index);
                        else engine.SetPointValue(edit.Index!.Value, edit.Value);
                        session.Publish(engine.GetSnapshot());
                        edit.Completion.TrySetResult();
                    }
                    catch (OperationCanceledException) { edit.Completion.TrySetCanceled(); }
                    catch (Exception ex) { edit.Completion.TrySetException(ex); }
                    continue;
                }
                token.ThrowIfCancellationRequested();
                if (!serial.IsConnected || serial.ReceiveSessionGeneration != lease.Generation)
                    throw new IOException("연결이 바뀌었어요. 장치 응답을 다시 시작하세요.");
                var chunk = ((ModbusSensorSession.Receive)work).Chunk;
                Interlocked.Add(ref queuedBytes, -chunk.Bytes.Length);
                if (lastChunk != 0 && chunk.ReceivedTimestamp > lastChunk &&
                    Stopwatch.GetElapsedTime(lastChunk, chunk.ReceivedTimestamp).TotalMilliseconds > Math.Max(100, serial.AppliedReceiveIdleTimeoutMs * 2)) framer.Clear();
                lastChunk = chunk.ReceivedTimestamp;
                foreach (var request in framer.Feed(chunk.Bytes, chunk.EndsAtNativeIdleBoundary))
                {
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref overflow) != 0) throw new IOException("시뮬레이터 수신 대기열 초과로 응답을 중지했어요.");
                    // FC05/06/22 echoes are indistinguishable from identical requests.
                    // Stop on a rapid echo instead of creating a self-sustaining TX loop.
                    // Compare RX time, not dequeue time: a retry queued before TX
                    // cannot be its echo, and a real echo may be processed much later.
                    if (previousResponse is not null && request.AsSpan().SequenceEqual(previousResponse) &&
                        chunk.ReceivedTimestamp >= lastWriteStarted &&
                        Stopwatch.GetElapsedTime(lastWriteCompleted, chunk.ReceivedTimestamp).TotalMilliseconds < 100)
                        throw new IOException("쓰기 응답의 로컬 에코 또는 너무 빠른 중복 요청을 감지했어요. 어댑터 에코를 끄고 요청 간격을 100ms 이상으로 설정하세요.");
                    var response = engine.Respond(request);
                    session.Publish(engine.GetSnapshot());
                    if (response is null) continue;
                    if (definition.ResponseDelayMs > 0) await Task.Delay(definition.ResponseDelayMs, token).ConfigureAwait(false);
                    await WaitForResponseSlot().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref overflow) != 0) throw new IOException("시뮬레이터 수신 대기열 초과로 응답을 중지했어요.");
                    // RX can arrive while SendAsync is still completing.
                    lastWriteStarted = Stopwatch.GetTimestamp();
                    await lease.SendAsync(response, token).ConfigureAwait(false);
                    lastWriteCompleted = Stopwatch.GetTimestamp();
                    previousResponse = response;
                    transmitted(response, lease.Generation); responded();
                }
            }
        }
        finally { serial.CaptureBytesReceived -= Receive; serial.StatusChanged -= Changed; session.Complete(); }
    }
}
