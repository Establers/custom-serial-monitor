using System.Reflection;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialExclusiveSessionTests
{
    [Fact]
    public async Task ReservationRequiresAConnectionAndRejectsEveryCompetingWriterAndDriverChange()
    {
        await using var serial = new SerialService();
        Assert.Throws<InvalidOperationException>(() => serial.AcquireExclusiveSession("Modbus"));
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        await using var lease = serial.AcquireExclusiveSession("Modbus");
        Assert.Equal("Modbus", serial.ExclusiveSessionOwner);
        Assert.Throws<InvalidOperationException>(() => serial.AcquireExclusiveSession("second"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SendAsync(new("test", "ping"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SendBytesAsync([1], "test", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SendTerminalInputAsync([1], lease.Generation, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.ChangeBaudRateAsync(19200, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.ConfigureAsync(
            SerialPortConfiguration.FromSettings(new() { PortName = "MOCK" }), lease.Generation, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SetControlLineAsync(SerialControlLine.Dtr, true, lease.Generation, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SetRtsToggleAsync(true, lease.Generation, default));
        Assert.Throws<InvalidOperationException>(() => serial.SetRawBridgePriorityEnabled(true));
        Assert.False(serial.IsRawBridgePriorityEnabled);
        Assert.Equal(0, serial.WrittenByteCount);
        Assert.Equal(0, serial.ConnectionErrorCount);
    }

    [Fact]
    public async Task OwnerCanWriteAndNormalWritersResumeOnlyAfterDisposal()
    {
        await using var serial = await Connected();
        var lease = serial.AcquireExclusiveSession("Modbus");
        Assert.Equal(serial.ReceiveSessionGeneration, lease.Generation);
        await lease.SendAsync([1, 3, 0, 0], default);
        Assert.Equal(4, serial.WrittenByteCount);
        await lease.DisposeAsync();
        Assert.Null(serial.ExclusiveSessionOwner);
        await serial.SendBytesAsync([7, 8], "normal", default);
        Assert.Equal(6, serial.WrittenByteCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.SendAsync([9], default));
    }

    [Fact]
    public async Task QueuedNormalSendPreventsReservationBeforeItTakesThePhysicalWriteGate()
    {
        await using var serial = await Connected();
        var gate = Gate(serial, "_writeGate");
        await gate.WaitAsync();
        var sending = serial.SendBytesAsync([1], "normal", default);
        try
        {
            Assert.False(sending.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => serial.AcquireExclusiveSession("Modbus"));
        }
        finally { gate.Release(); }
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        await using var lease = serial.AcquireExclusiveSession("Modbus");
    }

    [Fact]
    public async Task QueuedDriverChangePreventsReservationBeforeItTakesTheLifecycleGate()
    {
        await using var serial = await Connected();
        var gate = Gate(serial, "_lifecycleGate");
        await gate.WaitAsync();
        var changing = serial.ChangeBaudRateAsync(19200, default);
        try
        {
            Assert.False(changing.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => serial.AcquireExclusiveSession("Modbus"));
        }
        finally { gate.Release(); }
        Assert.Equal(19200, await changing.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var lease = serial.AcquireExclusiveSession("Modbus");
    }

    [Fact]
    public async Task ClosingLeaseDrainsAdmittedWritesAndRejectsNewWritesUntilTheDrainEnds()
    {
        await using var serial = await Connected();
        var lease = serial.AcquireExclusiveSession("Modbus");
        var gate = Gate(serial, "_writeGate");
        await gate.WaitAsync();
        var sending = lease.SendAsync([1, 2], default);
        var closing = lease.DisposeAsync().AsTask();
        try
        {
            Assert.False(closing.IsCompleted);
            Assert.Equal("Modbus", serial.ExclusiveSessionOwner);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.SendAsync([3], default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SendBytesAsync([4], "normal", default));
            Assert.Throws<InvalidOperationException>(() => serial.AcquireExclusiveSession("second"));
        }
        finally { gate.Release(); }
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(serial.ExclusiveSessionOwner);
        await serial.SendBytesAsync([4], "normal", default);
        Assert.Equal(3, serial.WrittenByteCount);
    }

    [Fact]
    public async Task CanceledQueuedOwnerWriteDoesNotLeakAdmission()
    {
        await using var serial = await Connected();
        var lease = serial.AcquireExclusiveSession("Modbus");
        var gate = Gate(serial, "_writeGate");
        await gate.WaitAsync();
        using var stop = new CancellationTokenSource();
        var sending = lease.SendAsync([1], stop.Token);
        var closing = lease.DisposeAsync().AsTask();
        try
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5)));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(serial.ExclusiveSessionOwner);
            Assert.Equal(0, serial.WrittenByteCount);
        }
        finally { gate.Release(); }
        await serial.SendBytesAsync([2], "normal", default);
    }

    [Fact]
    public async Task OwnerCannotSendIntoAReplacementConnection()
    {
        await using var serial = await Connected();
        await using var lease = serial.AcquireExclusiveSession("Modbus");
        await serial.DisconnectAsync(default);
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        Assert.NotEqual(lease.Generation, serial.ReceiveSessionGeneration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.SendAsync([1], default));
        Assert.Equal(0, serial.WrittenByteCount);
        Assert.True(serial.IsConnected);
    }

    [Fact]
    public async Task OwnerWriteQueuedBeforeDisconnectCannotCrossTheConnectionBoundary()
    {
        await using var serial = await Connected();
        await using var lease = serial.AcquireExclusiveSession("Modbus");
        var gate = Gate(serial, "_writeGate");
        await gate.WaitAsync();
        var sending = lease.SendAsync([1, 2], default);
        var disconnecting = serial.DisconnectAsync(default);
        gate.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5)));
        await disconnecting.WaitAsync(TimeSpan.FromSeconds(5));
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        Assert.Equal(0, serial.WrittenByteCount);
        Assert.True(serial.IsConnected);
    }

    [Fact]
    public async Task ActiveBridgeRejectsReservationAndTurningItOffAllowsReservation()
    {
        await using var serial = await Connected();
        serial.SetRawBridgePriorityEnabled(true);
        Assert.Throws<InvalidOperationException>(() => serial.AcquireExclusiveSession("Modbus"));
        serial.SetRawBridgePriorityEnabled(false);
        await using var lease = serial.AcquireExclusiveSession("Modbus");
    }

    [Fact]
    public async Task OwnerCanRetireAnUnverifiedConnectionBeforeReleasingTheReservation()
    {
        await using var serial = await Connected();
        await using var lease = serial.AcquireExclusiveSession("Modbus");
        await lease.DisconnectAsync(default);
        Assert.False(serial.IsConnected);
        Assert.Equal("Modbus", serial.ExclusiveSessionOwner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.SendAsync([1], default));
    }

    [Fact]
    public async Task OldOwnersCleanupCannotDisconnectAReplacementConnection()
    {
        await using var serial = await Connected();
        await using var lease = serial.AcquireExclusiveSession("Modbus");
        await serial.DisconnectAsync(default);
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var replacementGeneration = serial.ReceiveSessionGeneration;
        await lease.DisconnectAsync(default);
        Assert.True(serial.IsConnected);
        Assert.Equal(replacementGeneration, serial.ReceiveSessionGeneration);
    }

    [Fact]
    public async Task LeaseDisposalWaitsForPendingConnectionRetirement()
    {
        await using var serial = await Connected();
        var lease = serial.AcquireExclusiveSession("Modbus");
        var gate = Gate(serial, "_lifecycleGate");
        await gate.WaitAsync();
        var retiring = lease.DisconnectAsync(default);
        var closing = lease.DisposeAsync().AsTask();
        try
        {
            Assert.False(retiring.IsCompleted);
            Assert.False(closing.IsCompleted);
            Assert.Equal("Modbus", serial.ExclusiveSessionOwner);
        }
        finally { gate.Release(); }
        await retiring.WaitAsync(TimeSpan.FromSeconds(5));
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(serial.IsConnected);
        Assert.Null(serial.ExclusiveSessionOwner);
    }

    [Fact]
    public async Task OnlyOneConcurrentReservationSucceeds()
    {
        await using var serial = await Connected();
        var contenders = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            try { return serial.AcquireExclusiveSession("owner " + index); }
            catch (InvalidOperationException) { return null; }
        })).ToArray();
        var winner = Assert.Single((await Task.WhenAll(contenders)).OfType<ISerialExclusiveSession>());
        await winner.DisposeAsync();
        Assert.Null(serial.ExclusiveSessionOwner);
    }

    [Fact]
    public async Task RepeatedOldLeaseDisposalCannotReleaseTheNextOwner()
    {
        await using var serial = await Connected();
        var old = serial.AcquireExclusiveSession("old");
        await old.DisposeAsync();
        await using var current = serial.AcquireExclusiveSession("current");
        await old.DisposeAsync();
        Assert.Equal("current", serial.ExclusiveSessionOwner);
        await current.SendAsync([1], default);
    }

    private static async Task<SerialService> Connected()
    {
        var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        return serial;
    }

    private static SemaphoreSlim Gate(SerialService serial, string name) =>
        (SemaphoreSlim)typeof(SerialService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
}
