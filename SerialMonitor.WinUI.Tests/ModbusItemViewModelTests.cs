using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusItemViewModelTests
{
    [Fact]
    public async Task MixedDefinitionsDecodeIntoStableNamedRowsAndLeaveUnspecifiedWordsVisible()
    {
        var monitor = new Monitor { Values = [253, 0x3FC0, 0, 7, 2, 8] };
        var model = Model(monitor);
        model.ApplySettings(new ModbusMonitorSettings { Address = 10, Quantity = 6, Items = [
            new() { Address = 10, Name = "Temperature", ValueType = ModbusValueType.Int16, Scale = .1, Unit = "°C" },
            new() { Address = 11, Name = "Reference", ValueType = ModbusValueType.Float32, Access = ModbusItemAccess.ReadWrite },
            new() { Address = 14, Name = "Mode", ValueNames = [new() { Value = 2, Name = "Automatic" }], Access = ModbusItemAccess.ReadWrite }
        ] });
        await model.RunAsync(false);
        Assert.Equal(new[] { "10", "11–12", "13", "14", "15" }, model.Rows.Select(row => row.AddressText));
        Assert.Equal("Temperature", model.Rows[0].NameText);
        Assert.Equal("25.3 °C", model.Rows[0].ValueText);
        Assert.Equal("1.5", model.Rows[1].ValueText);
        Assert.Equal("7", model.Rows[2].ValueText);
        Assert.Equal("Automatic (2)", model.Rows[3].ValueText);
        var row = model.Rows[1];
        model.Refresh();
        Assert.Same(row, model.Rows[1]);
    }

    [Fact]
    public async Task TypedWritesApplyScaleAndDoNotChangeLegacyRawMode()
    {
        var monitor = new Monitor { Values = [3] };
        var model = Model(monitor);
        model.ApplySettings(new ModbusMonitorSettings { Quantity = 1, Items = [new() { Address = 0, Name = "Setpoint", Scale = .1, Access = ModbusItemAccess.ReadWrite }] });
        await model.RunAsync(false);
        model.SelectedRow = model.Rows[0];
        model.WriteModeIndex = 1;
        Assert.Equal("0.3", model.WriteValuesText);
        await model.WriteAsync();
        Assert.Equal(new ushort[] { 3 }, Assert.Single(monitor.Writes).Values);
        model.WriteValuesText = "25.3";
        Assert.Contains("0x00FD", model.WritePreview);
        Assert.Contains("HEX:", model.WritePreview);
        await model.WriteAsync();
        Assert.Equal(new ushort[] { 253 }, monitor.Writes[1].Values);
        model.WriteModeIndex = 0;
        model.WriteValuesText = "25";
        await model.WriteAsync();
        Assert.Equal(new ushort[] { 25 }, monitor.Writes[2].Values);
    }

    [Fact]
    public async Task ValueNamesEncodeDecodedKeysAndFloatWritesPreserveConfiguredWordOrder()
    {
        var monitor = new Monitor();
        var model = Model(monitor);
        model.ApplySettings(new ModbusMonitorSettings { Quantity = 3, Items = [
            new() { Address = 0, Name = "Mode", Access = ModbusItemAccess.ReadWrite, ValueNames = [new() { Value = 2, Name = "Automatic" }] },
            new() { Address = 1, ValueType = ModbusValueType.Float32, SwapWords = true, Access = ModbusItemAccess.ReadWrite }
        ] });
        model.WriteModeIndex = 1;
        model.WriteValuesText = "Automatic";
        await model.WriteAsync();
        Assert.Equal(new ushort[] { 2 }, monitor.Writes[0].Values);
        model.WriteAddressText = "1";
        model.WriteValuesText = "1.5";
        await model.WriteAsync();
        Assert.Equal(1, monitor.Writes[1].Address);
        Assert.Equal(new ushort[] { 0, 0x3FC0 }, monitor.Writes[1].Values);
    }

    [Fact]
    public async Task RawModeCannotBypassReadOnlyOrPartialPairRestrictionsAndPreviewIsSafe()
    {
        var monitor = new Monitor();
        var model = Model(monitor);
        model.ApplySettings(new ModbusMonitorSettings { Quantity = 3, Items = [
            new() { Address = 0, Name = "Actual value" },
            new() { Address = 1, ValueType = ModbusValueType.Int32, Access = ModbusItemAccess.ReadWrite }
        ] });
        model.WriteValuesText = "1";
        Assert.NotEmpty(model.WritePreview);
        await model.WriteAsync();
        Assert.NotEmpty(model.Error);
        model.WriteAddressText = "1";
        await model.WriteAsync();
        Assert.NotEmpty(model.Error);
        model.WriteModeIndex = 1;
        model.WriteValuesText = "1.5";
        await model.WriteAsync();
        Assert.NotEmpty(model.Error);
        Assert.Empty(monitor.Writes);
    }

    [Fact]
    public async Task DefinitionEditingIsOfflineClonedAndInvalidImportsCanBeCorrected()
    {
        var monitor = new Monitor();
        var model = Model(monitor);
        var item = new ModbusItemDefinition { Address = 0, Name = "Saved name", Access = ModbusItemAccess.ReadWrite };
        model.ApplyItemDefinitions([item]);
        item.Name = "External mutation";
        Assert.Equal("Saved name", model.CreateSettingsSnapshot().Items[0].Name);
        var snapshot = model.CreatePersistableSettingsSnapshot();
        snapshot.Items[0].Name = "Another mutation";
        Assert.Equal("Saved name", model.CreateSettingsSnapshot().Items[0].Name);
        model.ApplySettings(new ModbusMonitorSettings { Items = [null!] });
        await model.RunAsync(false);
        Assert.Empty(monitor.Reads);
        Assert.NotNull(model.CreateItemEditorSettings());
        model.ApplyItemDefinitions([]);
        Assert.Empty(model.CreateSettingsSnapshot().Items);
        Assert.Empty(monitor.Writes);
    }

    [Fact]
    public void DialogResultCannotBeAppliedToAChangedTargetOrLoadedProfile()
    {
        var model = Model(new Monitor());
        var revision = model.SettingsRevision;
        model.UnitText = "2";
        Assert.Throws<InvalidOperationException>(() => model.ApplyItemDefinitions([new() { Address = 0, Name = "Old device" }], revision));
        Assert.Empty(model.CreateSettingsSnapshot().Items);
        revision = model.SettingsRevision;
        model.ApplySettings(new ModbusMonitorSettings { UnitId = 2, Items = [new() { Address = 0, Name = "Loaded profile" }] });
        Assert.Throws<InvalidOperationException>(() => model.ApplyItemDefinitions([], revision));
        Assert.Equal("Loaded profile", model.CreateSettingsSnapshot().Items[0].Name);
    }

    private static ModbusRtuViewModel Model(Monitor monitor) => new(monitor, () => true, () => Task.CompletedTask, () => { });

    private sealed class Monitor : IModbusRtuMonitorService
    {
        private ModbusMonitorSnapshot _snapshot = new(0, ModbusMonitorState.Idle, false, false, null, null, 0, 0, null, null, false);
        public ushort[]? Values { get; init; }
        public bool IsBusy => false;
        public List<ModbusMonitorSettings> Reads { get; } = [];
        public List<(int Address, ushort[] Values)> Writes { get; } = [];
        public ModbusMonitorSnapshot GetSnapshot() => _snapshot;
        public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken)
        {
            Reads.Add(settings.Clone());
            _snapshot = _snapshot with { Revision = _snapshot.Revision + 1, Values = Values ?? new ushort[settings.Quantity], ValuesCurrent = true, UpdatedAt = DateTimeOffset.Now };
            return Task.CompletedTask;
        }
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        {
            Writes.Add((address, values.ToArray()));
            return Task.CompletedTask;
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
