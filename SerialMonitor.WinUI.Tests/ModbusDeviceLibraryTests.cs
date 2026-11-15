using System.Reflection;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusDeviceLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SerialMonitor-device-tests-" + Guid.NewGuid().ToString("N"));
    private string Example => Path.Combine(AppContext.BaseDirectory, "docs", "modbus_sensor_ee071.json");
    private ModbusDeviceLibrary Library => new(_directory, Example);
    private const string Json = """{"schemaVersion":1,"name":"Test device","unitId":1,"points":[{"name":"Setpoint","area":3,"address":0,"initialValue":"25","writable":true}]}""";
    private ModbusSensorViewModel Model(IModbusDeviceLibrary? library = null)
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        return new(new(serial, (_, _) => { }), () => true, () => { }, library: library ?? Library);
    }

    [Fact] public async Task BuiltInLoadsWithoutCreatingUserFilesAndCannotBeOverwrittenOrDeleted()
    {
        var result = await Library.LoadAsync(default);
        var example = Assert.Single(result.Items);
        Assert.True(example.IsBuiltIn); Assert.Equal("ee071", example.Id);
        Assert.Contains("EE071", example.Label); Assert.Equal(12, example.Definition.Points.Count);
        Assert.Empty(result.Warning); Assert.False(Directory.Exists(_directory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Library.SaveAsync(Json, example, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Library.DeleteAsync(example, default));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact] public async Task SavedEditedAndDeletedProductsSurviveReloadWithoutChangingBuiltIn()
    {
        var initial = await Library.SaveAsync(Json, null, default);
        var loaded = (await Library.LoadAsync(default)).Items.Single(p => !p.IsBuiltIn);
        Assert.Equal(initial.Id, loaded.Id); Assert.Equal(initial.Json, loaded.Json);
        var changed = await Library.SaveAsync(initial.Json.Replace("25", "30"), loaded, default);
        Assert.Equal(initial.Id, changed.Id);
        Assert.Equal("30", changed.Definition.Points[0].InitialValue);
        Assert.True(File.Exists(Path.Combine(_directory, initial.Id + ".json.bak")));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        await Library.DeleteAsync(changed, default);
        Assert.True(Assert.Single((await Library.LoadAsync(default)).Items).IsBuiltIn);
    }

    [Fact] public async Task EditorAndExportKeepKoreanLabelsReadable()
    {
        var model = Model(); await model.InitializeLibraryAsync();
        Assert.Contains("새 장치", model.GetEditorJson(true));
        var product = await Library.SaveAsync(Json.Replace("Test device", "시험 장치"), null, default);
        Assert.Contains("시험 장치", product.Json);
        Assert.Equal("시험 장치", ModbusSensorDefinition.Parse(product.Json).Name);
    }

    [Fact] public async Task StaleEditOrDeleteNeverOverwritesAnotherWritersChanges()
    {
        var old = await Library.SaveAsync(Json, null, default);
        var newer = await Library.SaveAsync(old.Json.Replace("25", "50"), old, default);
        await Assert.ThrowsAsync<IOException>(() => Library.SaveAsync(old.Json, old, default));
        await Assert.ThrowsAsync<IOException>(() => Library.DeleteAsync(old, default));
        Assert.Equal(newer.Json, (await Library.LoadAsync(default)).Items.Single(p => !p.IsBuiltIn).Json);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("""{"schemaVersion":1,"name":"Bad","unitId":0,"points":[]}""")]
    public async Task InvalidSaveLeavesExistingProductUntouched(string invalid)
    {
        var product = await Library.SaveAsync(Json, null, default);
        await Assert.ThrowsAnyAsync<Exception>(() => Library.SaveAsync(invalid, product, default));
        Assert.Equal(product.Json, (await Library.LoadAsync(default)).Items.Single(p => !p.IsBuiltIn).Json);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact] public async Task CorruptedFilesProduceWarningWhileValidProductsRemainAvailable()
    {
        var saved = await Library.SaveAsync(Json, null, default);
        var badPath = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(badPath, "{");
        var loaded = await Library.LoadAsync(default);
        Assert.Equal(2, loaded.Items.Count); Assert.Contains(Path.GetFileName(badPath), loaded.Warning);
        Assert.Equal("{", await File.ReadAllTextAsync(badPath));
        Assert.Contains(loaded.Items, p => p.Id == saved.Id);
    }

    [Fact] public async Task OversizedImportAndCancelledSaveDoNotChangeCatalog()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "large-input.txt");
        await File.WriteAllTextAsync(path, new string('x', 262145));
        await Assert.ThrowsAsync<IOException>(() => ModbusDeviceLibrary.ReadJsonFileAsync(path, default));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Library.SaveAsync(Json, null, stop.Token));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.json"));
    }

    [Fact] public async Task LibraryRejectsAnExternalPathAsProductIdentity()
    {
        var outside = new ModbusDevicePreset("../outside", Json, ModbusSensorDefinition.Parse(Json));
        await Assert.ThrowsAsync<ArgumentException>(() => Library.DeleteAsync(outside, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Library.SaveAsync(Json, outside, default));
    }

    [Fact] public async Task ProductListSelectsBuiltInAndDuplicatesIntoEditablePersistentCopy()
    {
        var model = Model(); await model.InitializeLibraryAsync();
        var builtin = model.SelectedProduct!;
        Assert.True(builtin.IsBuiltIn); Assert.False(model.CanDeleteSelected);
        Assert.Equal(12, model.Points.Count); Assert.False(model.IsBusy);
        await model.DuplicateProductAsync();
        var copy = model.SelectedProduct!;
        Assert.False(copy.IsBuiltIn); Assert.True(model.CanDeleteSelected);
        Assert.NotEqual(builtin.Definition.Name, copy.Definition.Name);
        Assert.Equal(builtin.Json, model.Products[0].Json);
        var second = Model(); await second.InitializeLibraryAsync();
        Assert.Equal(2, second.Products.Count);
        second.SelectedProduct = second.Products.Single(p => p.Id == copy.Id);
        await second.DeleteProductAsync();
        Assert.True(second.SelectedProduct!.IsBuiltIn);
        await model.ReloadLibraryAsync();
        Assert.Single(model.Products); Assert.True(model.SelectedProduct!.IsBuiltIn);
    }

    [Fact] public async Task ImportAddsUniquelyNamedProductsAndInvalidImportKeepsSelection()
    {
        var model = Model(); await model.InitializeLibraryAsync();
        await model.ImportProductAsync(Example); await model.ImportProductAsync(Example);
        Assert.Equal(3, model.Products.Count);
        Assert.Equal(3, model.Products.Select(p => p.Definition.Name).Distinct().Count());
        var selected = model.SelectedProduct;
        await Assert.ThrowsAnyAsync<Exception>(() => model.SaveProductAsync("{}"));
        Assert.Same(selected, model.SelectedProduct); Assert.True(model.CanManage);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact] public async Task IdleRefreshDoesNotInvalidateRoleSelectionOrPreview()
    {
        var model = Model(); await model.InitializeLibraryAsync();
        await using var analyzer = new ProtocolAnalyzer();
        var protocol = new ProtocolViewModel(analyzer) { Sensor = model, SelectedViewIndex = 4 };
        Assert.Equal(model.Status, protocol.WorkspaceHeaderHelp);
        model.Refresh();
        var modelEvents = new List<string?>(); var protocolEvents = new List<string?>();
        model.PropertyChanged += (_, e) => modelEvents.Add(e.PropertyName);
        protocol.PropertyChanged += (_, e) => protocolEvents.Add(e.PropertyName);
        var points = model.Points; var menu = protocol.PresentationOptions;
        for (var i = 0; i < 1000; i++) model.Refresh();
        Assert.Empty(modelEvents); Assert.Empty(protocolEvents);
        Assert.Same(points, model.Points); Assert.Same(menu, protocol.PresentationOptions);
    }

    [Fact] public async Task ConnectionAndFeedbackUseTheExistingHeaderWithoutChangingPreview()
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        var available = false;
        var model = new ModbusSensorViewModel(new(serial,(_,_)=>{}),()=>available,()=>{},()=>"상단에서 COM 포트를 연결하세요",Library);
        await model.InitializeLibraryAsync();
        await using var analyzer = new ProtocolAnalyzer();
        var protocol = new ProtocolViewModel(analyzer) { Sensor=model, SelectedViewIndex=4 };
        var points = model.Points;
        Assert.Equal("상단에서 COM 포트를 연결하세요",protocol.WorkspaceHeader);
        model.ShowFeedback("제품을 저장했어요.");
        Assert.Equal("제품을 저장했어요.",protocol.WorkspaceHeader);
        Assert.Equal(protocol.WorkspaceHeader,protocol.WorkspaceHeaderHelp);
        model.ClearFeedback(); available = true; model.Refresh();
        Assert.Equal(model.Status,protocol.WorkspaceHeader);
        Assert.Same(points,model.Points);
        var running = model.StartAsync();
        available = false;
        ((ModbusRtuMonitorServiceTests.TestSerial)(object)serial).Disconnect();
        await running;
        Assert.StartsWith("응답 중지:", protocol.WorkspaceHeader);
    }

    [Fact] public async Task PendingSaveBlocksStartAndSelectionUntilItCompletes()
    {
        var blocking = new BlockingLibrary(Library);
        var model = Model(blocking); await model.InitializeLibraryAsync();
        var selected = model.SelectedProduct;
        var saving = model.SaveProductAsync(Json);
        try
        {
            Assert.False(model.CanManage); Assert.False(model.StartCommand.CanExecute(null));
            model.SelectedProduct = null; Assert.Same(selected, model.SelectedProduct);
            await model.StartAsync(); Assert.False(model.IsBusy);
        }
        finally { blocking.Release.TrySetResult(); await saving; }
        Assert.True(model.CanManage); Assert.True(model.StartCommand.CanExecute(null));
        Assert.Equal("Test device", model.SelectedProduct!.Definition.Name);
    }

    [Fact] public async Task RunningDeviceLocksProductChangesAndPublishesOnlyRealBusyTransitions()
    {
        var model = Model(); await model.InitializeLibraryAsync();
        var busyEvents = 0;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.IsBusy)) busyEvents++; };
        var selected = model.SelectedProduct;
        var running = model.StartAsync();
        try
        {
            Assert.True(model.IsBusy); Assert.False(model.CanManage);
            model.SelectedProduct = null; Assert.Same(selected, model.SelectedProduct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => model.SaveProductAsync(Json));
            for (var i = 0; i < 1000; i++) model.Refresh();
            Assert.Equal(1, busyEvents);
        }
        finally { await model.StopAsync(); await running; }
        Assert.Equal(2, busyEvents); Assert.True(model.CanManage);
    }

    [Theory]
    [InlineData(0,0)] [InlineData(1,1)] [InlineData(2,2)] [InlineData(3,4)] [InlineData(4,3)]
    public async Task AdjacentModbusMenuPreservesInternalRoleIdentity(int menuIndex, int role)
    {
        await using var analyzer = new ProtocolAnalyzer();
        var model = new ProtocolViewModel(analyzer);
        model.SelectedPresentationIndex = menuIndex;
        Assert.Equal(role, model.SelectedViewIndex);
        Assert.Equal(menuIndex, model.SelectedPresentationIndex);
        Assert.Equal(role == 4, model.IsSensorSelected);
    }

    private sealed class BlockingLibrary(IModbusDeviceLibrary inner) : IModbusDeviceLibrary
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ModbusDeviceLibraryResult> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
        public async Task<ModbusDevicePreset> SaveAsync(string json, ModbusDevicePreset? previous, CancellationToken token)
        { await Release.Task.WaitAsync(token); return await inner.SaveAsync(json, previous, token); }
        public Task DeleteAsync(ModbusDevicePreset preset, CancellationToken token) => inner.DeleteAsync(preset, token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
