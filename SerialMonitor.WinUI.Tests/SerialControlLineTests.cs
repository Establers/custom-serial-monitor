using System.ComponentModel;
using System.Reflection;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialControlLineTests
{
    [Fact]
    public void PinnedSerialLibraryExposesTheExpectedSafeHandleContract() =>
        Assert.True(WindowsSerialControlLines.HasCompatibleHandleContract);

    [Theory]
    [InlineData(SerialControlLine.Dtr, SerialControlLineMode.Off)]
    [InlineData(SerialControlLine.Dtr, SerialControlLineMode.On)]
    [InlineData(SerialControlLine.Dtr, SerialControlLineMode.Handshake)]
    [InlineData(SerialControlLine.Rts, SerialControlLineMode.Off)]
    [InlineData(SerialControlLine.Rts, SerialControlLineMode.On)]
    [InlineData(SerialControlLine.Rts, SerialControlLineMode.Handshake)]
    [InlineData(SerialControlLine.Rts, SerialControlLineMode.Toggle)]
    public void UpdatingOneDcbModePreservesEveryOtherFlag(SerialControlLine line, SerialControlLineMode mode)
    {
        const uint original = 0xA5A5A5A5;
        var changed = WindowsSerialControlLines.WithMode(original, line, mode);
        var shift = line == SerialControlLine.Dtr ? 4 : 12;
        Assert.Equal(original & ~(3u << shift), changed & ~(3u << shift));
        var decoded = WindowsSerialControlLines.DecodeModes(changed);
        Assert.Equal(mode, line == SerialControlLine.Dtr ? decoded.Dtr : decoded.Rts);
    }

    [Fact]
    public async Task LiveManualOutputsAndTxToggleKeepTheSameRxSession()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK", RtsEnable = true }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        var reader = serial.ReceivedBytes;
        Assert.Equal(SerialModemStatusKind.Mock, serial.ControlLineStatus.InputStatus);
        Assert.Null(serial.ControlLineStatus.Cts);

        var dtr = await serial.SetControlLineAsync(SerialControlLine.Dtr, true, generation, default);
        Assert.Equal(SerialControlLineMode.On, dtr.Dtr);
        Assert.Equal(SerialControlLineMode.On, dtr.Rts);
        var toggle = await serial.SetRtsToggleAsync(true, generation, default);
        Assert.Equal(SerialControlLineMode.Toggle, toggle.Rts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SetControlLineAsync(SerialControlLine.Rts, false, generation, default));
        var manual = await serial.SetRtsToggleAsync(false, generation, default);
        Assert.Equal(SerialControlLineMode.On, manual.Rts); // Restore the chosen manual setting.
        await serial.SetControlLineAsync(SerialControlLine.Rts, false, generation, default);
        await serial.SendBytesAsync([0xAA, 0x55], "control-test", default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = new List<byte>();
        while (!System.Text.Encoding.UTF8.GetString(received.ToArray()).Contains("control-test", StringComparison.Ordinal))
            received.AddRange((await reader.ReadAsync(timeout.Token)).Bytes);
        Assert.Contains("control-test", System.Text.Encoding.UTF8.GetString(received.ToArray()));
        Assert.Equal(2, serial.WrittenByteCount);
        Assert.Equal(generation, serial.ReceiveSessionGeneration);
        Assert.Same(reader, serial.ReceivedBytes);
        await serial.DisconnectAsync(default);
        Assert.Equal(SerialControlLineStatus.Disconnected, serial.ControlLineStatus);
    }

    [Theory]
    [InlineData(SerialHandshakeMode.None, false, false)]
    [InlineData(SerialHandshakeMode.XOn, false, false)]
    [InlineData(SerialHandshakeMode.Rts, false, true)]
    [InlineData(SerialHandshakeMode.Dtr, true, false)]
    [InlineData(SerialHandshakeMode.RtsXOn, false, true)]
    [InlineData(SerialHandshakeMode.DtrXOn, true, false)]
    [InlineData(SerialHandshakeMode.DtrRts, true, true)]
    [InlineData(SerialHandshakeMode.DtrRtsXOn, true, true)]
    public async Task HardwareHandshakeLocksOnlyItsOwnedOutputs(SerialHandshakeMode handshake, bool ownsDtr, bool ownsRts)
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK", Handshake = handshake }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        foreach (var (line, owned) in new[] { (SerialControlLine.Dtr, ownsDtr), (SerialControlLine.Rts, ownsRts) })
        {
            if (owned)
                await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SetControlLineAsync(line, true, generation, default));
            else
                await serial.SetControlLineAsync(line, true, generation, default);
        }
        if (ownsRts)
            await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SetRtsToggleAsync(true, generation, default));
        else
            await serial.SetRtsToggleAsync(true, generation, default);
        Assert.True(serial.IsConnected);
        Assert.Equal(0, serial.ConnectionErrorCount);
    }

    [Fact]
    public async Task StaleGenerationCannotMutateAReconnectedPort()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var previous = serial.ReceiveSessionGeneration;
        await serial.DisconnectAsync(default);
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SetControlLineAsync(SerialControlLine.Dtr, true, previous, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SetRtsToggleAsync(true, previous, default));
        Assert.Equal(SerialControlLineMode.Off, serial.ControlLineStatus.Dtr);
        Assert.Equal(SerialControlLineMode.Off, serial.ControlLineStatus.Rts);
    }

    [Fact]
    public async Task CancelingAQueuedControlChangeLeavesTheOutputsUntouched()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var writeGate = (SemaphoreSlim)typeof(SerialService).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        await writeGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var change = serial.SetControlLineAsync(SerialControlLine.Dtr, true, serial.ReceiveSessionGeneration, cancellation.Token);
            Assert.False(change.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(SerialControlLineMode.Off, serial.ControlLineStatus.Dtr);
        }
        finally { writeGate.Release(); }
    }

    [Fact]
    public async Task InvalidToggleHandshakeCombinationDoesNotOpenAConnection()
    {
        await using var serial = new SerialService();
        await Assert.ThrowsAsync<ArgumentException>(() => serial.ConnectAsync(new()
            { PortName = "MOCK", Handshake = SerialHandshakeMode.Rts, RtsToggleOnTransmit = true }, new(), default));
        Assert.False(serial.IsConnected);
    }

    [Fact]
    public async Task LateDriverUpdateIsBoundedAndRetiredBeforeAnotherConnectionCanOpen()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        var session = typeof(SerialService).GetField("_currentReceiveSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        var runtime = (SerialControlLineRuntime)session.GetType().GetProperty("ControlLines")!.GetValue(session)!;
        using var release = new ManualResetEventSlim();
        var port = new BlockingWritePort(release);
        typeof(SerialControlLineRuntime).GetField("_port", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, port);
        var changing = serial.SetControlLineAsync(SerialControlLine.Dtr, true, generation, default);
        try
        {
            await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<TimeoutException>(() => changing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(SerialConnectionState.Faulted, serial.ConnectionState);
            Assert.Equal(SerialControlLineStatus.Disconnected, serial.ControlLineStatus);
            var disconnecting = serial.DisconnectAsync(default);
            Assert.False(disconnecting.IsCompleted); // The late native call still owns the handle.
            release.Set();
            await disconnecting.WaitAsync(TimeSpan.FromSeconds(3));
            await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
            Assert.NotEqual(generation, serial.ReceiveSessionGeneration);
            Assert.Equal(SerialControlLineMode.Off, serial.ControlLineStatus.Dtr);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void StaleInputSnapshotBecomesUnknownInsteadOfContinuingToShowOn()
    {
        var runtime = new SerialControlLineRuntime(new FakePort(), 1, new(), default, () => { });
        typeof(SerialControlLineRuntime).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime,
            new SerialControlLineStatus(1, SerialControlLineMode.On, SerialControlLineMode.On,
                SerialModemStatusKind.Available, true, true, true, true));
        typeof(SerialControlLineRuntime).GetField("_lastObservedTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime,
            System.Diagnostics.Stopwatch.GetTimestamp() - 3 * System.Diagnostics.Stopwatch.Frequency);
        Assert.Null(runtime.Status.Cts);
        Assert.Equal(SerialControlLineMode.Unknown, runtime.Status.Dtr);
        Assert.Equal(SerialModemStatusKind.Error, runtime.Status.InputStatus);
        Assert.Contains("taking too long", runtime.Status.Error);
    }

    [Theory]
    [InlineData(1, SerialModemStatusKind.Unsupported)]
    [InlineData(50, SerialModemStatusKind.Unsupported)]
    [InlineData(5, SerialModemStatusKind.Error)]
    public async Task FailedInputQueriesShowUnknownAndRecoverWithoutReconnect(int errorCode, SerialModemStatusKind expected)
    {
        var port = new FakePort { InputError = new Win32Exception(errorCode) };
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = new SerialControlLineRuntime(port, 7, new(), lifetime.Token, () => { });
        runtime.Start();
        try
        {
            await UntilAsync(() => runtime.Status.InputStatus == expected && runtime.Status.Error is not null, lifetime.Token);
            Assert.Null(runtime.Status.Cts);
            Assert.Null(runtime.Status.Dsr);
            Assert.Null(runtime.Status.Dcd);
            Assert.Null(runtime.Status.Ri);
            port.InputError = null;
            await UntilAsync(() => runtime.Status.InputStatus == SerialModemStatusKind.Available, lifetime.Token);
            Assert.True(runtime.Status.Cts);
            Assert.False(runtime.Status.Dsr);
            Assert.True(runtime.Status.Dcd);
            Assert.False(runtime.Status.Ri);
            Assert.Null(runtime.Status.Error);
        }
        finally { lifetime.Cancel(); await runtime.Worker; }
    }

    [Fact]
    public void RejectedDriverUpdateRestoresAndVerifiesPreviousModes()
    {
        var port = new FakePort { RejectOnce = true };
        var error = Assert.Throws<SerialControlLineUpdateException>(() => SerialControlLineUpdate.Apply(
            SerialControlLine.Dtr, SerialControlLineMode.On, port.ReadModes, port.WriteMode));
        Assert.True(error.StateRestored);
        Assert.Equal((SerialControlLineMode.Off, SerialControlLineMode.Off), port.ReadModes());
        Assert.Equal(2, port.Writes);
    }

    [Fact]
    public void FailedRestorationIsReportedAsUnverified()
    {
        var error = Assert.Throws<SerialControlLineUpdateException>(() => SerialControlLineUpdate.Apply(
            SerialControlLine.Dtr, SerialControlLineMode.On,
            () => (SerialControlLineMode.Off, SerialControlLineMode.Off),
            (_, _) => throw new IOException("unavailable")));
        Assert.False(error.StateRestored);
    }

    [Fact]
    public async Task TxToggleSettingsRoundTripAndConflictsAreNormalized()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialLineProfileTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profile.json");
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.SerialSettings.RtsToggleOnTransmit = true;
            Assert.True(profile.SerialSettings.Clone().RtsToggleOnTransmit);
            await service.SaveAsync(path, profile, default);
            Assert.True((await service.LoadAsync(path, default)).SerialSettings.RtsToggleOnTransmit);
            profile.SerialSettings.Handshake = SerialHandshakeMode.Rts;
            await service.SaveAsync(path, profile, default);
            Assert.False((await service.LoadAsync(path, default)).SerialSettings.RtsToggleOnTransmit);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(10, token);
    }

    private sealed class FakePort : ISerialControlLinePort
    {
        public Exception? InputError;
        public bool RejectOnce;
        public int Writes;
        private SerialControlLineMode _dtr, _rts;
        public (SerialControlLineMode Dtr, SerialControlLineMode Rts) ReadModes() => (_dtr, _rts);
        public (bool Cts, bool Dsr, bool Dcd, bool Ri) ReadInputs()
        {
            if (InputError is { } error) throw error;
            return (true, false, true, false);
        }
        public void WriteMode(SerialControlLine line, SerialControlLineMode mode)
        {
            Writes++;
            if (RejectOnce) { RejectOnce = false; return; } // A driver silently ignoring the requested mode.
            if (line == SerialControlLine.Dtr) _dtr = mode;
            else _rts = mode;
        }
    }

    private sealed class BlockingWritePort(ManualResetEventSlim release) : ISerialControlLinePort
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SerialControlLineMode _dtr;
        public (SerialControlLineMode Dtr, SerialControlLineMode Rts) ReadModes() => (_dtr, SerialControlLineMode.Off);
        public (bool Cts, bool Dsr, bool Dcd, bool Ri) ReadInputs() => (false, false, false, false);
        public void WriteMode(SerialControlLine line, SerialControlLineMode mode)
        {
            Started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test driver wasn't released.");
            _dtr = mode;
        }
    }
}
