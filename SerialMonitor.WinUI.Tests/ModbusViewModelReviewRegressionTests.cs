using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusViewModelReviewRegressionTests
{
    [Fact]
    public async Task RejectedReadCannotRelabelPreviousTargetValuesOrVerificationWithTheNewAddress()
    {
        var service = new ControlledMonitor();
        var model = Model(service);
        var previousVerification = await VerifyInitialTarget(model, service);
        model.AddressText = "100";
        service.RejectNextRead = true;

        await model.RunAsync(false);

        Assert.NotEmpty(model.Error);
        Assert.Equal(1, service.ReadCount);
        Assert.Equal("100", model.Rows[0].AddressText);
        AssertNoOldTargetValues(model, previousVerification);
        service.PulseOldSnapshot();
        model.Refresh();
        AssertNoOldTargetValues(model, previousVerification);
    }

    [Fact]
    public async Task RejectedIdleWriteCannotRelabelPreviousTargetValuesOrVerificationWithTheNewAddress()
    {
        var service = new ControlledMonitor();
        var model = Model(service);
        var previousVerification = await VerifyInitialTarget(model, service);
        model.AddressText = "100";
        model.WriteAddressText = "100";
        model.WriteValuesText = "99";
        service.RejectNextWrite = true;

        await model.WriteAsync();

        Assert.NotEmpty(model.Error);
        Assert.Equal(0, service.WrittenAddress);
        Assert.Equal("100", model.Rows[0].AddressText);
        AssertNoOldTargetValues(model, previousVerification);
        service.PulseOldSnapshot();
        model.Refresh();
        AssertNoOldTargetValues(model, previousVerification);
    }

    private static async Task<string> VerifyInitialTarget(ModbusRtuViewModel model, ControlledMonitor service)
    {
        model.QuantityText = "2";
        await model.RunAsync(false);
        model.SelectedRow = model.Rows[0];
        var writing = model.WriteAsync();
        service.CompleteWrite();
        await writing;
        Assert.Equal("000B", model.Rows[0].RawText);
        return model.Status;
    }

    private static void AssertNoOldTargetValues(ModbusRtuViewModel model, string previousVerification)
    {
        Assert.All(model.Rows, row =>
        {
            Assert.Equal("—", row.RawText);
            Assert.Equal("—", row.ValueText);
        });
        Assert.NotEqual(previousVerification, model.Status);
        Assert.Empty(model.Statistics);
    }

    [Fact]
    public async Task SelectingAnotherRowDuringWriteSynchronizesEditorAfterCompletionWithoutRetargetingInFlightWrite()
    {
        var service = new ControlledMonitor();
        var model = Model(service);
        model.QuantityText = "2";
        await model.RunAsync(false);
        model.SelectedRow = model.Rows[0];

        var writing = model.WriteAsync();
        Assert.False(writing.IsCompleted);
        model.SelectedRow = model.Rows[1];
        Assert.Equal("0", model.WriteAddressText);
        service.CompleteWrite();
        await writing;

        Assert.Equal(0, service.WrittenAddress);
        Assert.Equal(new ushort[] { 10 }, service.WrittenValues);
        Assert.Same(model.Rows[1], model.SelectedRow);
        Assert.Equal("1", model.WriteAddressText);
        Assert.Equal("22", model.WriteValuesText);
    }

    [Fact]
    public async Task SameSelectionKeepsTheExplicitWriteDraftAfterCompletion()
    {
        var service = new ControlledMonitor();
        var model = Model(service);
        model.QuantityText = "2";
        await model.RunAsync(false);
        model.SelectedRow = model.Rows[0];
        model.WriteValuesText = "99";
        var writing = model.WriteAsync();
        model.SelectedRow = model.Rows[0];
        service.CompleteWrite();
        await writing;

        Assert.Equal(new ushort[] { 99 }, service.WrittenValues);
        Assert.Equal("0", model.WriteAddressText);
        Assert.Equal("99", model.WriteValuesText);
    }

    [Fact]
    public async Task IdleAvailabilityIsPublishedOnlyAfterTheNewSelectedEditorIsConsistent()
    {
        var service = new ControlledMonitor();
        ModbusRtuViewModel? model = null;
        var observeCompletion = false;
        var idleNotifications = 0;
        model = new ModbusRtuViewModel(service, () => true, () => Task.CompletedTask, () =>
        {
            if (!observeCompletion || model!.IsBusy) return;
            idleNotifications++;
            Assert.Equal("1", model.WriteAddressText);
            Assert.Equal("22", model.WriteValuesText);
        });
        model.QuantityText = "2";
        await model.RunAsync(false);
        model.SelectedRow = model.Rows[0];
        var writing = model.WriteAsync();
        model.Refresh(); // An ordinary status tick observes the active write.
        model.SelectedRow = model.Rows[1];
        observeCompletion = true;
        service.CompleteWrite();
        await writing;

        Assert.True(idleNotifications > 0);
        Assert.Equal(0, service.WrittenAddress);
    }

    [Fact]
    public async Task RowEditorPropertyNotificationsExposeTheNewAddressAndValuesTogether()
    {
        var model = Model(new ControlledMonitor());
        model.QuantityText = "2";
        await model.RunAsync(false);
        model.SelectedRow = model.Rows[0];
        var addressNotifications = 0;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(model.WriteAddressText)) return;
            addressNotifications++;
            Assert.Equal("1", model.WriteAddressText);
            Assert.Equal("20", model.WriteValuesText);
        };

        model.SelectedRow = model.Rows[1];

        Assert.Equal(1, addressNotifications);
    }

    [Fact]
    public async Task EditingTheReadTargetDoesNotKeepAnotherDevicesWriteVerificationOrStatistics()
    {
        var service = new ControlledMonitor();
        var model = Model(service);
        var ready = model.Status;
        model.QuantityText = "2";
        await model.RunAsync(false);
        model.SelectedRow = model.Rows[0];
        var writing = model.WriteAsync();
        service.CompleteWrite();
        await writing;
        Assert.NotEqual(ready, model.Status);
        Assert.NotEmpty(model.Statistics);

        model.UnitText = "2";
        Assert.Empty(model.Rows);
        Assert.Equal(ready, model.Status);
        Assert.Empty(model.Statistics);
        service.PulseOldSnapshot();
        model.Refresh();
        Assert.Equal(ready, model.Status);
        Assert.Empty(model.Statistics);
        Assert.Equal(1, service.ReadCount);
    }

    private static ModbusRtuViewModel Model(ControlledMonitor service) => new(service, () => true, () => Task.CompletedTask, () => { });
    private sealed class ControlledMonitor : IModbusRtuMonitorService
    {
        private ModbusMonitorSnapshot _snapshot = new(0, ModbusMonitorState.Idle, false, false, null, null, 0, 0, null, null, false);
        private readonly TaskCompletionSource _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsBusy => _snapshot.IsBusy;
        public int ReadCount { get; private set; }
        public bool RejectNextRead { get; set; }
        public bool RejectNextWrite { get; set; }
        public int WrittenAddress { get; private set; }
        public ushort[] WrittenValues { get; private set; } = [];
        public ModbusMonitorSnapshot GetSnapshot() => _snapshot;
        public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken)
        {
            if (RejectNextRead) throw new InvalidOperationException("Read admission rejected.");
            ReadCount++;
            _snapshot = new(_snapshot.Revision + 1, ModbusMonitorState.Stopped, false, false, [10, 20], DateTimeOffset.Now, 1, 0, 2, null, true);
            return Task.CompletedTask;
        }
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        {
            if (RejectNextWrite) throw new InvalidOperationException("Write admission rejected.");
            WrittenAddress = address;
            WrittenValues = values.ToArray();
            return CompleteWriteAsync(cancellationToken);
        }
        private async Task CompleteWriteAsync(CancellationToken cancellationToken)
        {
            await _write.Task.WaitAsync(cancellationToken);
            _snapshot = _snapshot with { Revision = _snapshot.Revision + 1, Values = [11, 22], WriteState = ModbusWriteState.Verified };
        }
        public void CompleteWrite() => _write.TrySetResult();
        public void PulseOldSnapshot() => _snapshot = _snapshot with { Revision = _snapshot.Revision + 1 };
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
