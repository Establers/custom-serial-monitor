using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusReadWorkflowTests
{
    [Theory]
    [InlineData("Modbus exception 0x02.", "address")]
    [InlineData("Modbus response timed out; reconnect before continuing.", "reconnect")]
    [InlineData("Modbus response CRC error; reconnect before continuing.", "CRC")]
    public void KnownCommunicationFailuresExplainTheNextCheck(string raw, string expected)
    {
        var help = ModbusRtuViewModel.DescribeCommunicationError(raw);
        Assert.NotEqual(raw, help);
        Assert.Contains(expected, help);
        Assert.Equal("Unrecognized failure", ModbusRtuViewModel.DescribeCommunicationError("Unrecognized failure"));
    }

    [Fact]
    public async Task IncludingAGroupUpdatesAutoReadScopeWithoutSendingAndKeepsSingleReadAvailable()
    {
        var service = new Monitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(new()
        {
            SelectedGroupId = "a", Groups = [new() { Id = "a", Name = "Temperature", Enabled = false, Settings = new() }]
        });
        Assert.False(model.StartCommand.CanExecute(null));
        Assert.True(model.ReadOnceCommand.CanExecute(null));
        Assert.Equal(0, model.EnabledGroupCount);
        Assert.Equal(Visibility.Visible, model.FirstReadVisibility);
        var revision = model.SettingsRevision;
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.SelectedGroupIncluded = true;
        Assert.True(model.StartCommand.CanExecute(null));
        Assert.Equal(1, model.EnabledGroupCount);
        Assert.True(model.SettingsRevision > revision);
        Assert.Contains(nameof(model.StartLabel), changed);
        Assert.Contains("Temperature", model.StartHelp);
        Assert.True(model.CreatePersistablePollingSettingsSnapshot()!.Groups[0].Enabled);
        Assert.Equal(0, service.Reads);

        await model.RunAsync(false);
        Assert.Equal(1, service.Reads);
        Assert.Equal(Visibility.Collapsed, model.FirstReadVisibility);
        Assert.Equal("250", model.Rows[0].ValueText);
    }

    [Fact]
    public async Task InclusionCannotChangeDuringReadingAndNeverChangesTheSelectedReadRange()
    {
        var service = new Monitor { Hold = true };
        var model = Model(service);
        model.ApplySavedPollingSettings(new()
        {
            SelectedGroupId = "b", Groups = [
                new() { Id = "a", Name = "A", Settings = new() { UnitId = 1 } },
                new() { Id = "b", Name = "B", Enabled = false, Settings = new() { UnitId = 2, Address = 32, Quantity = 2 } }
            ]
        });
        var reading = model.RunAsync(false);
        Assert.True(model.IsBusy);
        model.SelectedGroupIncluded = true;
        Assert.False(model.SelectedGroupIncluded);
        Assert.Equal((2, 32, 2), (service.Last!.UnitId, service.Last.Address, service.Last.Quantity));
        await model.StopAsync();
        await reading;
        Assert.Equal(1, service.Reads);
        Assert.False(model.CreatePersistablePollingSettingsSnapshot()!.Groups[1].Enabled);
    }

    [Fact]
    public void RangePreviewHandlesHexAndIncompleteEditsWithoutInventingAnAddressRange()
    {
        var model = Model(new Monitor());
        model.UnitText = "0x02";
        model.AddressText = "0x1000";
        model.QuantityText = "0x04";
        Assert.Contains("ID 2", model.TargetSettingsSummary);
        Assert.Contains("4096–4099", model.TargetSettingsSummary);
        Assert.Contains(model.TargetSettingsSummary, model.ReadOnceHelp);
        model.AddressText = "65535";
        Assert.DoesNotContain("65538", model.TargetSettingsSummary);
        model.QuantityText = "";
        Assert.NotEmpty(model.TargetSettingsSummary);
    }

    [Fact]
    public void RowsDistinguishUnrequestedReadsFromReadingAndMissingResponses()
    {
        var row = new ModbusRegisterRow(0, 0, 1, 1);
        var settings = new ModbusMonitorSettings();
        var before = row.StatusText;
        row.UpdateReadState(null, settings, false, null, ModbusMonitorState.Reading);
        var reading = row.StatusText;
        Assert.NotEqual(before, reading);
        row.UpdateReadState(null, settings, false, null, ModbusMonitorState.Error);
        Assert.NotEqual(before, row.StatusText);
        Assert.NotEqual(reading, row.StatusText);
        row.UpdateReadState([250], settings, true, DateTimeOffset.Now, ModbusMonitorState.Stopped);
        Assert.Equal("250", row.ValueText);
        row.UpdateReadState(null, settings, false, null, ModbusMonitorState.Idle);
        Assert.Equal(before, row.StatusText);
        Assert.Null(row.RawValues);
        Assert.Equal("—", row.ValueText);
        Assert.Equal("—", row.UpdatedText);
    }

    private static ModbusRtuViewModel Model(Monitor service) => new(service, () => true, () => Task.CompletedTask, () => { });
    private sealed class Monitor : IModbusRtuMonitorService
    {
        private ModbusMonitorSnapshot _snapshot = new(0, ModbusMonitorState.Idle, false, false, null, null, 0, 0, null, null, false);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold { get; init; }
        public int Reads { get; private set; }
        public ModbusMonitorSettings? Last { get; private set; }
        public bool IsBusy => _snapshot.IsBusy;
        public ModbusMonitorSnapshot GetSnapshot() => _snapshot;
        public async Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken)
        {
            Reads++;
            Last = settings.Clone();
            _snapshot = new(1, ModbusMonitorState.Reading, true, false, null, null, 0, 0, null, null, false);
            if (Hold) await _completion.Task.WaitAsync(cancellationToken);
            _snapshot = new(2, ModbusMonitorState.Stopped, false, false, Enumerable.Repeat((ushort)250, settings.Quantity).ToArray(), DateTimeOffset.Now, 1, 0, 2, null, true);
        }
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync() { _completion.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
