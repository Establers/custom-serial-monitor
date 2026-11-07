using System.Buffers.Binary;
using System.Text.Json;
using SerialMonitor.Core.Protocols;
using SerialMonitor.Core.Protocols.VirtualModbus;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class VirtualModbusDeviceViewModelTests
{
    private static VirtualModbusRegisterDefinition Point(int address = 0) => new()
    { Name = $"Point {address}", Area = "holding", Address = address, DataType = "uint16", Value = JsonSerializer.SerializeToElement(1) };
    private static VirtualModbusDefinition Map(int id, int count = 1) => new()
    { SchemaVersion = 1, Devices = [new() { Name = $"Sensor {id}", UnitId = id, Registers = Enumerable.Range(0, count).Select(Point).ToList() }] };
    private static byte[] Adu(string body)
    {
        var bytes = Convert.FromHexString(body); var result = new byte[bytes.Length + 2]; bytes.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(bytes.Length), ModbusRtuCrc.Compute(bytes)); return result;
    }

    [Fact]
    public async Task JsonAddsSensors_AndDuplicateImportIsAtomic()
    {
        var store = new VirtualModbusDeviceStore();
        await using var runtime = new VirtualModbusDeviceRuntime(store, (_, _, _) => Task.CompletedTask);
        var model = new VirtualModbusDeviceViewModel(store, runtime, () => runtime.Start(1, 1.75));
        await model.ImportTextAsync(VirtualModbusJson.Serialize(Map(1)));
        await model.ImportTextAsync(VirtualModbusJson.Serialize(Map(2)));
        Assert.Equal(2, model.Devices.Count);
        await model.ImportTextAsync(VirtualModbusJson.Serialize(Map(1)));
        Assert.Equal(2, model.Devices.Count); Assert.Contains("already loaded", model.Message);
    }

    [Fact]
    public async Task LiveValueAndIdEditing_ChangesRealResponses_WithoutOverwritingDraft()
    {
        var store = new VirtualModbusDeviceStore(); store.Add(Map(1));
        await using var runtime = new VirtualModbusDeviceRuntime(store, (_, _, _) => Task.CompletedTask);
        var model = new VirtualModbusDeviceViewModel(store, runtime, () => runtime.Start(1, 1.75));
        model.Refresh(true); await model.ToggleAsync(); Assert.True(model.IsRunning);
        model.SelectedRegister = model.Registers[0]; model.EditValue = "250";
        model.Refresh(); Assert.Equal("250", model.EditValue);
        model.ApplyValue(); Assert.Equal("250", model.Registers[0].CurrentValue);
        model.EditUnitId = "7"; model.ChangeId();
        Assert.Null(store.ProcessRequest(Adu("010300000001")).Response);
        Assert.Equal(Adu("07030200FA"), store.ProcessRequest(Adu("070300000001")).Response);
        Assert.True(model.IsRunning);
        await model.ToggleAsync(); Assert.False(model.IsRunning);
    }

    [Fact]
    public async Task RemoveOnLaterPage_SelectsRemainingDeviceAndResetsPage()
    {
        var store = new VirtualModbusDeviceStore(); store.Add(Map(1, 130)); store.Add(Map(2));
        await using var runtime = new VirtualModbusDeviceRuntime(store, (_, _, _) => Task.CompletedTask);
        var model = new VirtualModbusDeviceViewModel(store, runtime, () => { });
        model.Refresh(true); model.Next(); Assert.Equal("2 / 2", model.PageText);
        model.Remove(); Assert.Equal(2, model.SelectedDevice!.UnitId); Assert.Equal("1 / 1", model.PageText); Assert.Single(model.Registers);
        model.Remove(); Assert.Empty(model.Registers);
        await model.ImportTextAsync(VirtualModbusJson.Serialize(Map(3)));
        Assert.Equal("1 / 1", model.PageText); Assert.Single(model.Registers);
    }

    [Fact]
    public async Task DeviceSwitchAndRefresh_KeepSelectionAndEditTargetConsistent()
    {
        var store = new VirtualModbusDeviceStore(); store.Add(Map(1)); store.Add(Map(2));
        await using var runtime = new VirtualModbusDeviceRuntime(store, (_, _, _) => Task.CompletedTask);
        var model = new VirtualModbusDeviceViewModel(store, runtime, () => { }); model.Refresh(true);
        model.SelectedDevice = model.Devices[1]; model.SelectedRegister = model.Registers[0]; model.EditValue = "99"; model.ApplyValue();
        Assert.Equal("1", store.Page(1, 0)[0].Value); Assert.Equal("99", store.Page(2, 0)[0].Value);
        model.Refresh(true); Assert.Equal(2, model.SelectedDevice!.UnitId);
        model.SelectedDevice = null; Assert.Equal(2, model.SelectedDevice!.UnitId);
    }

    [Fact]
    public async Task ForcedRefresh_PreservesDraftAcrossListViewSelectionReset()
    {
        var store = new VirtualModbusDeviceStore(); store.Add(Map(1, 130));
        await using var runtime = new VirtualModbusDeviceRuntime(store, (_, _, _) => Task.CompletedTask);
        var model = new VirtualModbusDeviceViewModel(store, runtime, () => { }); model.Refresh(true);
        model.SelectedRegister = model.Registers[0]; model.EditValue = "77";
        model.Registers.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) model.SelectedRegister = null;
        };
        model.Refresh(true);
        Assert.Equal(0, model.SelectedRegister!.Index); Assert.Equal("77", model.EditValue);
        model.Next(); Assert.Null(model.SelectedRegister); Assert.Equal("", model.EditValue);
    }

    [Fact]
    public async Task InvalidLiveValue_DoesNotChangeMemory_AndHasError()
    {
        var store = new VirtualModbusDeviceStore(); store.Add(Map(1));
        await using var runtime = new VirtualModbusDeviceRuntime(store, (_, _, _) => Task.CompletedTask);
        var model = new VirtualModbusDeviceViewModel(store, runtime, () => { }); model.Refresh(true);
        model.SelectedRegister = model.Registers[0]; model.EditValue = "65536"; model.ApplyValue();
        Assert.Equal("1", store.Page(1, 0)[0].Value); Assert.Contains("range", model.Message);
    }

    [Fact]
    public async Task MultiFileImport_SaveAndReload_PreservesChangedValuesAndIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "serial-vmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "first.json"); var second = Path.Combine(directory, "second.json"); var saved = Path.Combine(directory, "saved.json");
            await File.WriteAllTextAsync(first, VirtualModbusJson.Serialize(Map(1, 4096)), new System.Text.UTF8Encoding(true));
            await File.WriteAllTextAsync(second, VirtualModbusJson.Serialize(Map(2, 4096)));
            var files = new VirtualModbusDeviceFileService();
            var store = new VirtualModbusDeviceStore(); store.Add(await files.LoadAsync([first, second], CancellationToken.None));
            store.SetValue(1, 0, "65535"); store.ChangeUnitId(2, 247);
            await files.SaveAsync(saved, store.Export(), CancellationToken.None);
            var restored = new VirtualModbusDeviceStore(); restored.Add(await files.LoadAsync([saved], CancellationToken.None));
            Assert.Equal(8192, restored.Devices().Sum(d => d.RegisterCount)); Assert.Equal("65535", restored.Page(1, 0)[0].Value);
            Assert.Contains(restored.Devices(), d => d.UnitId == 247);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task DeployedExampleAndGptGuide_ArePresentAndImportable()
    {
        var example = Path.Combine(AppContext.BaseDirectory, "docs", "virtual_modbus_device.example.json");
        var definition = await new VirtualModbusDeviceFileService().LoadAsync([example], CancellationToken.None);
        Assert.Equal(2, definition.Devices.Count);
        var guide = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "docs", "virtual_modbus_device_gpt.md"));
        Assert.Contains("rawWords", guide); Assert.Contains("40001", guide); Assert.Contains("추측하지", guide);
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "docs", "virtual_modbus_device.schema.json")));
        Assert.Equal(1, schema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
    }
}
