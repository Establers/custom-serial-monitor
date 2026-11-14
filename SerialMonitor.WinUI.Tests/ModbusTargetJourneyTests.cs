using Microsoft.UI.Xaml;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusTargetJourneyTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("orphan")]
    [InlineData("null-selection")]
    [InlineData("missing-settings")]
    [InlineData("invalid-settings")]
    public async Task LeavingAPopulatedGroupPublishesTheEmptySetupVisibility(string transition)
    {
        var service = new JourneyMonitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(Workspace());
        await model.RunAsync(false);
        Assert.Equal(Visibility.Collapsed, model.EmptyVisibility);
        Assert.Equal("100", model.Rows[0].ValueText);
        var published = new List<Visibility>();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.EmptyVisibility)) published.Add(model.EmptyVisibility);
        };

        if (transition == "null-selection") model.SelectedPollingGroup = null;
        else
        {
            var next = Workspace();
            switch (transition)
            {
                case "empty": next.Groups.Clear(); next.SelectedGroupId = null; break;
                case "orphan": next.SelectedGroupId = "missing"; break;
                case "missing-settings": next.Groups[0].Settings = null!; break;
                case "invalid-settings": next.Groups[0].Settings.Address = 65536; break;
            }
            model.ApplySavedPollingSettings(next);
        }

        Assert.Empty(model.Rows);
        Assert.Equal(Visibility.Visible, model.EmptyVisibility);
        Assert.NotEmpty(published);
        Assert.All(published, visibility => Assert.Equal(Visibility.Visible, visibility));
        Assert.Single(service.Reads);
        Assert.Empty(service.Writes);
    }

    [Fact]
    public void SwitchingBetweenValidGroupsPublishesEmptyThenPopulatedVisibility()
    {
        var model = Model(new JourneyMonitor());
        model.ApplySavedPollingSettings(Workspace());
        var published = new List<Visibility>();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.EmptyVisibility)) published.Add(model.EmptyVisibility);
        };
        model.SelectedPollingGroup = model.PollingGroups[1];
        Assert.Equal(new[] { Visibility.Visible, Visibility.Collapsed }, published);
        Assert.Equal("2", model.UnitText);
        Assert.Equal("32", model.WriteAddressText);
    }

    [Theory]
    [InlineData("unit", 0)]
    [InlineData("function", 0)]
    [InlineData("address", 0)]
    [InlineData("quantity", 0)]
    [InlineData("unit", 1)]
    [InlineData("function", 1)]
    [InlineData("address", 1)]
    [InlineData("quantity", 1)]
    public void EditingAMonitorTargetClearsTheOldWriteDraftAndAlignsItsAddress(string target, int mode)
    {
        var service = new JourneyMonitor();
        var model = Model(service);
        model.ApplySettings(Settings());
        model.WriteModeIndex = mode;
        model.WriteAddressText = "17";
        model.WriteValuesText = mode == 0 ? "7 8" : "25.3";

        switch (target)
        {
            case "unit": model.UnitText = "2"; break;
            case "function": model.FunctionIndex = 0; break;
            case "address": model.AddressText = "0x20"; break;
            case "quantity": model.QuantityText = "8"; break;
        }

        Assert.Equal("", model.WriteValuesText);
        Assert.Equal(model.AddressText, model.WriteAddressText);
        Assert.Equal(mode, model.WriteModeIndex);
        Assert.Empty(service.Reads);
        Assert.Empty(service.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TimingDisplayAndExplicitWriteAddressEditsKeepTheWriteDraft(int mode)
    {
        var model = Model(new JourneyMonitor());
        model.ApplySettings(Settings());
        model.WriteModeIndex = mode;
        model.WriteAddressText = "17";
        model.WriteValuesText = mode == 0 ? "7 8" : "25.3";
        var expected = model.WriteValuesText;
        model.IntervalText = "2000";
        model.TimeoutText = "2000";
        model.FormatIndex = 4;
        model.ScaleText = "2";
        model.SwapBytes = true;
        model.SwapWords = true;
        model.UnitText = "1";
        model.AddressText = "16";
        model.QuantityText = "4";
        model.FunctionIndex = 2;
        Assert.Equal(expected, model.WriteValuesText);
        Assert.Equal("17", model.WriteAddressText);
        model.WriteAddressText = "18";
        Assert.Equal(expected, model.WriteValuesText);
    }

    [Fact]
    public async Task RejectedTargetEditsDuringAnAdmittedWriteKeepTheDraftAndCapturedRequest()
    {
        var service = new JourneyMonitor { HoldWrite = true };
        var model = Model(service);
        model.ApplySettings(Settings());
        model.WriteAddressText = "17";
        model.WriteValuesText = "7 8";
        var writing = model.WriteAsync();
        model.UnitText = "2";
        model.FunctionIndex = 0;
        model.AddressText = "32";
        model.QuantityText = "8";
        model.WriteAddressText = "33";
        model.WriteValuesText = "9";

        Assert.Equal("1", model.UnitText);
        Assert.Equal(2, model.FunctionIndex);
        Assert.Equal("16", model.AddressText);
        Assert.Equal("4", model.QuantityText);
        Assert.Equal("17", model.WriteAddressText);
        Assert.Equal("7 8", model.WriteValuesText);
        var sent = Assert.Single(service.Writes);
        Assert.Equal((1, (byte)3, 16, 4), (sent.Settings.UnitId, sent.Settings.Function, sent.Settings.Address, sent.Settings.Quantity));
        Assert.Equal(17, sent.Address);
        Assert.Equal(new ushort[] { 7, 8 }, sent.Values);
        service.CompleteWrite();
        await writing;
        model.UnitText = "2";
        Assert.Equal("", model.WriteValuesText);
        Assert.Equal(new ushort[] { 7, 8 }, sent.Values);
    }

    private static ModbusRtuViewModel Model(JourneyMonitor service) => new(service, () => true, () => Task.CompletedTask, () => { });
    private static ModbusMonitorSettings Settings() => new()
    {
        Address = 16, Quantity = 4,
        Items = [new() { Address = 16, Scale = 0.1, Access = ModbusItemAccess.ReadWrite }]
    };
    private static ModbusPollingSettings Workspace() => new()
    {
        SelectedGroupId = "a", Groups = [
            new() { Id = "a", Name = "First", Settings = new() { UnitId = 1, Address = 16, Quantity = 2 } },
            new() { Id = "b", Name = "Second", Settings = new() { UnitId = 2, Address = 32, Quantity = 1 } }
        ]
    };

    private sealed class JourneyMonitor : IModbusRtuMonitorService
    {
        private ModbusMonitorSnapshot _snapshot = new(0, ModbusMonitorState.Idle, false, false, null, null, 0, 0, null, null, false);
        private TaskCompletionSource? _write;
        public bool HoldWrite;
        public bool IsBusy => _snapshot.IsBusy;
        public List<ModbusMonitorSettings> Reads { get; } = [];
        public List<(ModbusMonitorSettings Settings, int Address, ushort[] Values)> Writes { get; } = [];
        public ModbusMonitorSnapshot GetSnapshot() => _snapshot;
        public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken)
        {
            Reads.Add(settings.Clone());
            _snapshot = new(_snapshot.Revision + 1, ModbusMonitorState.Stopped, false, false,
                Enumerable.Range(settings.UnitId * 100, settings.Quantity).Select(value => (ushort)value).ToArray(),
                DateTimeOffset.Now, 1, 0, 1, null, true);
            return Task.CompletedTask;
        }
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        {
            Writes.Add((settings.Clone(), address, values.ToArray()));
            _snapshot = _snapshot with { Revision = _snapshot.Revision + 1, IsBusy = HoldWrite, WriteState = ModbusWriteState.Writing };
            _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!HoldWrite) CompleteWrite();
            return _write.Task;
        }
        public void CompleteWrite()
        {
            _snapshot = _snapshot with { Revision = _snapshot.Revision + 1, IsBusy = false, WriteState = ModbusWriteState.Verified };
            _write!.TrySetResult();
        }
        public Task StopAsync() { CompleteWrite(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
