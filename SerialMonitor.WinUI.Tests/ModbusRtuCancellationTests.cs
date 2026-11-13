using System.Buffers.Binary;
using System.Reflection;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusRtuCancellationTests
{
    [Fact]
    public async Task CancelingAQueuedWriteDuringItsQuietWaitCannotTransmitIt()
    {
        var (serial, device) = Device();
        var firstSent = Signal();
        byte[]? firstRequest = null;
        var sends = 0;
        device.OnSend = packet =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                firstRequest = packet;
                firstSent.TrySetResult();
            }
            else device.Emit(packet[1] == 3 ? ReadReply(packet) : WriteAck(packet));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunAsync(Settings(), true, default);
        using var writeCancellation = new CancellationTokenSource();
        try
        {
            await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var write = service.WriteAsync(Settings(), 10, [123], writeCancellation.Token);
            device.PauseNextQuietWait = 1;
            device.Emit(ReadReply(firstRequest!));
            // The fake driver pauses the worker after the write starts its quiet
            // phase, before the transport's SendAsync can be admitted.
            await device.QuietEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writeCancellation.Cancel();
            device.QuietRelease.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await run;
            Assert.All(device.Sent, packet => Assert.Equal(3, packet[1]));
            Assert.DoesNotContain(service.GetSnapshot().WriteState,
                new[] { ModbusWriteState.Queued, ModbusWriteState.Writing });
            Assert.True(device.Connected);
            Assert.Null(device.Owner);
        }
        finally
        {
            device.QuietRelease.Set();
            await service.StopAsync();
            await Record.ExceptionAsync(() => run);
            device.QuietRelease.Dispose();
        }
    }

    [Fact]
    public async Task StopBeforeQueuedWriteClearsItsPendingStateWithoutTransmission()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunAsync(Settings(), true, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var write = service.WriteAsync(Settings(), 10, [123], default);
        Assert.Equal(ModbusWriteState.Queued, service.GetSnapshot().WriteState);
        var stopping = service.StopAsync();
        Assert.False(stopping.IsCompleted);
        Assert.NotNull(device.Owner);
        device.Emit(ReadReply(request!));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await run;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Single(device.Sent);
        Assert.DoesNotContain(service.GetSnapshot().WriteState,
            new[] { ModbusWriteState.Queued, ModbusWriteState.Writing });
        Assert.True(device.Connected);
    }

    [Fact]
    public async Task CancelingAStandaloneWriteBeforeTransmissionReportsCancellationAndClearsPendingState()
    {
        var (serial, device) = Device();
        device.PauseNextQuietWait = 1;
        await using var service = new ModbusRtuMonitorService(serial);
        using var cancellation = new CancellationTokenSource();
        var writing = service.WriteAsync(Settings(), 10, [123], cancellation.Token);
        try
        {
            await device.QuietEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            device.QuietRelease.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(device.Sent);
            Assert.DoesNotContain(service.GetSnapshot().WriteState,
                new[] { ModbusWriteState.Queued, ModbusWriteState.Writing });
            Assert.False(service.IsBusy);
            Assert.True(device.Connected);
            Assert.Null(device.Owner);
        }
        finally
        {
            device.QuietRelease.Set();
            await service.StopAsync();
            await Record.ExceptionAsync(() => writing);
            device.QuietRelease.Dispose();
        }
    }

    [Fact]
    public async Task StopAfterAcknowledgementBeforeReadbackPreservesTheUnverifiedWriteResult()
    {
        var (serial, device) = Device();
        device.OnSend = packet =>
        {
            Assert.Equal(6, packet[1]);
            device.PauseNextQuietWait = 1;
            device.Emit(WriteAck(packet));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var writing = service.WriteAsync(Settings(), 10, [123], default);
        try
        {
            await device.QuietEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ModbusWriteState.NotVerified, service.GetSnapshot().WriteState);
            var stopping = service.StopAsync();
            Assert.NotNull(device.Owner);
            device.QuietRelease.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(6, Assert.Single(device.Sent)[1]);
            Assert.Equal(ModbusWriteState.NotVerified, service.GetSnapshot().WriteState);
            Assert.False(service.GetSnapshot().ValuesCurrent);
            Assert.True(device.Connected);
            Assert.Null(device.Owner);
        }
        finally
        {
            device.QuietRelease.Set();
            await service.StopAsync();
            await Record.ExceptionAsync(() => writing);
            device.QuietRelease.Dispose();
        }
    }

    [Fact]
    public async Task CancelingAnAlreadyTransmittedQueuedWriteStillSettlesItsAcknowledgementAndReadback()
    {
        var (serial, device) = Device();
        var firstSent = Signal();
        var writeSent = Signal();
        byte[]? initialRead = null, writeRequest = null;
        device.OnSend = packet =>
        {
            if (packet[1] == 6)
            {
                writeRequest = packet;
                writeSent.TrySetResult();
            }
            else if (initialRead is null)
            {
                initialRead = packet;
                firstSent.TrySetResult();
            }
            else device.Emit(Crc([packet[0], packet[1], 4, 0, 123, 0, 2]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        using var writeCancellation = new CancellationTokenSource();
        var run = service.RunAsync(Settings(), true, default);
        await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var writing = service.WriteAsync(Settings(), 10, [123], writeCancellation.Token);
        device.Emit(ReadReply(initialRead!));
        await writeSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        writeCancellation.Cancel();
        Assert.False(writing.IsCompleted);
        Assert.NotNull(device.Owner);
        device.Emit(WriteAck(writeRequest!));
        await writing.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await run;
        Assert.Single(device.Sent.Where(packet => packet[1] == 6));
        Assert.True(device.Sent.Count(packet => packet[1] == 3) >= 2);
        Assert.Equal(ModbusWriteState.Verified, service.GetSnapshot().WriteState);
        Assert.True(device.Connected);
        Assert.Null(device.Owner);
    }

    [Fact]
    public async Task CancelingTheRunStillOwnsTheOutstandingReplyUntilItIsConsumed()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        using var cancellation = new CancellationTokenSource();
        var run = service.RunAsync(Settings(), true, cancellation.Token);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.True(service.IsBusy);
        Assert.NotNull(device.Owner);
        Assert.False(run.IsCompleted);
        device.Emit(ReadReply(request!));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(device.Sent);
        Assert.False(service.IsBusy);
        Assert.Null(device.Owner);
        Assert.True(device.Connected);
        Assert.False(service.GetSnapshot().ValuesCurrent);
    }

    [Theory]
    [InlineData("get_SerialFrameErrorCount")]
    [InlineData("get_SerialParityErrorCount")]
    [InlineData("get_SerialOverrunErrorCount")]
    [InlineData("get_SerialRxOverErrorCount")]
    public async Task ALineErrorDuringAnOtherwiseValidResponseFailsClosed(string counter)
    {
        var (serial, device) = Device();
        device.ErrorCounter = counter;
        device.OnSend = packet =>
        {
            Interlocked.Increment(ref device.LineErrors);
            device.Emit(ReadReply(packet));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunAsync(Settings(), false, default));
        Assert.False(device.Connected);
        Assert.True(device.WasOwnedWhenDisconnected);
        Assert.Null(device.Owner);
        Assert.Null(service.GetSnapshot().Values);
        Assert.False(service.GetSnapshot().ValuesCurrent);
        Assert.Single(device.Sent);
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("function")]
    [InlineData("trailing-same-chunk")]
    [InlineData("trailing-next-chunk")]
    public async Task UnexpectedResponseCannotBecomeAValidValueOrLeakIntoTheNextRequest(string fault)
    {
        var (serial, device) = Device();
        device.OnSend = packet =>
        {
            var response = ReadReply(packet);
            switch (fault)
            {
                case "unit": response[0]++; response = Crc(response[..^2]); break;
                case "function": response[1] = 4; response = Crc(response[..^2]); break;
                case "trailing-same-chunk": response = [.. response, 0xAA]; break;
            }
            device.Emit(response);
            if (fault == "trailing-next-chunk") device.Emit([0xAA]);
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunAsync(Settings(), true, default));
        Assert.False(device.Connected);
        Assert.True(device.WasOwnedWhenDisconnected);
        Assert.Null(device.Owner);
        Assert.Single(device.Sent);
        Assert.Equal(0, service.GetSnapshot().SuccessCount);
        Assert.Equal(1, service.GetSnapshot().ErrorCount);
        Assert.Null(service.GetSnapshot().Values);
    }

    [Fact]
    public async Task RepeatedPollingNeverStartsTheNextExchangeBeforeThePreviousResponse()
    {
        var (serial, device) = Device();
        var sent = new[] { Signal(), Signal(), Signal() };
        var outstanding = 0;
        var overlap = 0;
        var count = 0;
        device.OnSend = _ =>
        {
            if (Interlocked.Increment(ref outstanding) != 1) Interlocked.Exchange(ref overlap, 1);
            var index = Interlocked.Increment(ref count) - 1;
            if (index < sent.Length) sent[index].TrySetResult();
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunAsync(Settings(), true, default);
        await sent[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Hold a real outstanding response beyond the requested poll interval.
        await Task.Delay(150);
        Assert.False(sent[1].Task.IsCompleted);
        Interlocked.Decrement(ref outstanding);
        device.Emit(ReadReply(device.Sent[0]));
        await sent[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Decrement(ref outstanding);
        device.Emit(ReadReply(device.Sent[1]));
        await sent[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = service.StopAsync();
        Interlocked.Decrement(ref outstanding);
        device.Emit(ReadReply(device.Sent[2]));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await run;
        Assert.Equal(0, overlap);
        Assert.Equal(3, device.Sent.Count);
        Assert.All(device.Sent, packet => Assert.Equal(device.Sent[0], packet));
        Assert.Equal(3, service.GetSnapshot().SuccessCount);
        Assert.Null(device.Owner);
        Assert.True(device.Connected);
    }

    private static ModbusMonitorSettings Settings() => new()
    { Address = 10, Quantity = 2, IntervalMs = 100, TimeoutMs = 2000 };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static byte[] ReadReply(byte[] request) => Crc([request[0], request[1], 4, 0, 1, 0, 2]);
    private static byte[] WriteAck(byte[] request) => request[1] is 5 or 6 ? request.ToArray() : Crc(request[..6]);
    private static byte[] Crc(byte[] data)
    {
        var frame = new byte[data.Length + 2];
        data.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(data.Length), ModbusRtuCrc.Compute(data));
        return frame;
    }

    private static (ISerialService Serial, ControlledSerial Device) Device()
    {
        var serial = DispatchProxy.Create<ISerialService, ControlledSerial>();
        return (serial, (ControlledSerial)(object)serial);
    }

    public class ControlledSerial : ModbusRtuMonitorServiceTests.TestSerial
    {
        public int PauseNextQuietWait;
        private int _pauseConnectionRead;
        public TaskCompletionSource QuietEntered { get; } = Signal();
        public ManualResetEventSlim QuietRelease { get; } = new(false);
        public string? ErrorCounter;
        public long LineErrors;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_AppliedBaudRate" && Interlocked.Exchange(ref PauseNextQuietWait, 0) != 0)
                Interlocked.Exchange(ref _pauseConnectionRead, 1);
            if (targetMethod.Name == "get_IsConnected" && Interlocked.Exchange(ref _pauseConnectionRead, 0) != 0)
            {
                QuietEntered.TrySetResult();
                if (!QuietRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test quiet-phase gate was not released.");
            }
            if (targetMethod.Name == ErrorCounter) return Interlocked.Read(ref LineErrors);
            return base.Invoke(targetMethod, args);
        }
    }
}
