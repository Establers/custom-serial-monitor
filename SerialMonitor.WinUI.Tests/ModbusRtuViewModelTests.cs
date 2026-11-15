using System.Globalization;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusRtuViewModelTests
{
    [Fact]
    public async Task ConstructionAndProfileRestoreNeverTransmitAndInvalidTargetRequiresCorrection()
    {
        var service = new FakeMonitor();
        var model = Model(service);
        model.ApplySettings(new ModbusMonitorSettings { UnitId = 0, Address = 65536 });
        Assert.Equal("0", model.UnitText);
        Assert.Equal("65536", model.AddressText);
        Assert.Empty(service.Reads);
        await model.RunAsync(true);
        Assert.Empty(service.Reads);
        Assert.NotEmpty(model.Error);
        model.ApplySettings(new ModbusMonitorSettings { Function = 99 });
        await model.RunAsync(false);
        Assert.Empty(service.Reads);
        model.FunctionIndex = 2;
        await model.RunAsync(false);
        Assert.Single(service.Reads);
    }

    [Fact]
    public async Task FullInputIsValidatedBeforeAnyAdmissionOrTransmission()
    {
        var service = new FakeMonitor();
        var model = Model(service);
        foreach (var bad in new[] { "65536", "0x10000", "-1", "1x", "00000000000000000000000065536" })
        {
            model.AddressText = bad;
            await model.RunAsync(false);
            Assert.Empty(service.Reads);
            Assert.NotEmpty(model.Error);
        }
        model.AddressText = "0x10";
        model.UnitText = "0x02";
        model.QuantityText = "0x04";
        await model.RunAsync(false);
        var read = Assert.Single(service.Reads);
        Assert.Equal(16, read.Settings.Address);
        Assert.Equal(2, read.Settings.UnitId);
        Assert.Equal(4, read.Settings.Quantity);
        Assert.False(read.Repeat);
    }

    [Fact]
    public async Task RepeatedSnapshotsKeepRowsSelectionAndRawValuesWhileErrorsMarkThemStale()
    {
        var service = new FakeMonitor { Values = [10, 20] };
        var model = Model(service);
        model.QuantityText = "2";
        await model.RunAsync(false);
        var row = model.Rows[1];
        model.SelectedRow = row;
        Assert.Equal("1", model.WriteAddressText);
        Assert.Equal("20", model.WriteValuesText);
        service.SetValues([10, 25], current: true);
        model.Refresh();
        Assert.Same(row, model.Rows[1]);
        Assert.Same(row, model.SelectedRow);
        Assert.Equal("25", row.ValueText);
        Assert.Equal("●", row.ChangeText);
        service.SetValues([10, 25], current: false);
        model.Refresh();
        Assert.Equal("25", row.ValueText);
        Assert.NotEqual("—", row.UpdatedText);
        Assert.DoesNotContain("Current", row.StatusText);
        for (var index = 0; index < 20; index++) model.Refresh();
        Assert.Same(row, model.Rows[1]);
        Assert.Same(row, model.SelectedRow);
    }

    [Theory]
    [InlineData(ModbusValueType.Float32, 0x41C8, 0x0000, "25")]
    [InlineData(ModbusValueType.UInt32, 0xFFFF, 0xFFFF, "4294967295")]
    [InlineData(ModbusValueType.Int32, 0xFFFF, 0xFFFF, "-1")]
    public async Task PairedValuesRetainBothAddressesAndRawRegisters(ModbusValueType type, ushort first, ushort second, string expected)
    {
        var service = new FakeMonitor { Values = [first, second, 7] };
        var model = Model(service);
        model.ApplySettings(new ModbusMonitorSettings { Address = 10, Quantity = 3, ValueType = type });
        await model.RunAsync(false);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal("10–11", model.Rows[0].AddressText);
        Assert.Equal(expected, model.Rows[0].ValueText);
        Assert.NotEqual("7", model.Rows[1].ValueText);
        model.SelectedRow = model.Rows[0];
        Assert.Equal($"{first} {second}", model.WriteValuesText);
    }

    [Fact]
    public async Task StandaloneWriteReservesMainTxSynchronouslyAndUsesUnscaledRawValues()
    {
        var service = new FakeMonitor();
        ModbusRtuViewModel? model = null;
        var availabilityPublished = false;
        model = new ModbusRtuViewModel(service, () => true, () => Task.CompletedTask, () => availabilityPublished = model?.IsBusy == true);
        model.ScaleText = "0.1";
        model.WriteValuesText = "25 0x10";
        service.BeforeWrite = () =>
        {
            Assert.True(model.IsBusy);
            Assert.False(model.IsSettingsEnabled);
            Assert.True(availabilityPublished);
        };
        await model.WriteAsync();
        Assert.Equal(new ushort[] { 25, 16 }, Assert.Single(service.Writes).Values);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ReadOnlyMalformedAndOutsideAreaWritesNeverTransmit()
    {
        var service = new FakeMonitor();
        var model = Model(service);
        model.FunctionIndex = 3;
        model.WriteValuesText = "1";
        await model.WriteAsync();
        Assert.Empty(service.Writes);
        model.FunctionIndex = 2;
        foreach (var bad in new[] { "65536", "1 0x10000", "1 2x", "-1", "" })
        {
            model.WriteValuesText = bad;
            await model.WriteAsync();
            Assert.Empty(service.Writes);
        }
        model.WriteValuesText = "1 2";
        model.WriteAddressText = "9";
        await model.WriteAsync();
        Assert.Empty(service.Writes);
        model.FunctionIndex = 0;
        model.WriteAddressText = "0";
        model.WriteValuesText = "2";
        await model.WriteAsync();
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task PollingCanQueueOwnWriteButSettingsAndProtocolSelectionRemainLocked()
    {
        var service = new FakeMonitor { HoldRun = true };
        var canStart = true;
        var model = new ModbusRtuViewModel(service, () => canStart, () => Task.CompletedTask, () => { });
        await using var analyzer = new ProtocolAnalyzer();
        var protocol = new ProtocolViewModel(analyzer) { Modbus = model, SelectedViewIndex = 2 };
        var run = model.RunAsync(true);
        canStart = false;
        model.Refresh();
        Assert.True(model.IsBusy);
        Assert.False(protocol.IsProtocolSelectionEnabled);
        Assert.False(protocol.IsModbusViewSelectionEnabled);
        protocol.SelectedProtocolIndex = 2;
        Assert.Equal(1, protocol.SelectedProtocolIndex);
        protocol.SelectedViewIndex = 2;
        Assert.Equal(Visibility.Visible, protocol.RegisterMonitorVisibility);
        protocol.SelectedViewIndex = 3;
        Assert.Equal(2, protocol.SelectedViewIndex);
        protocol.SelectedViewIndex = 0;
        Assert.Equal(2, protocol.SelectedViewIndex);
        protocol.SelectedViewIndex = 1;
        Assert.Equal(2, protocol.SelectedViewIndex);
        Assert.Equal(Visibility.Collapsed, protocol.PacketAnalysisVisibility);
        Assert.Equal(Visibility.Visible, model.BusyVisibility);
        Assert.Contains(model.RequestSummary, protocol.WorkspaceHeader);
        Assert.Single(service.Reads);
        model.AddressText = "20";
        Assert.Equal("0", model.AddressText);
        model.WriteValuesText = "1";
        await model.WriteAsync();
        Assert.Single(service.Writes);
        await model.StopAsync();
        await run;
        Assert.False(model.IsBusy);
        Assert.True(protocol.IsProtocolSelectionEnabled);
        protocol.SelectedViewIndex = 1;
        Assert.Equal($"{protocol.Activity} · {protocol.RetainedSummary}", protocol.WorkspaceHeader);
        protocol.SelectedViewIndex = 3;
        Assert.Equal(2, protocol.SelectedProtocolIndex);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 1, true)]
    [InlineData(3, 2, false)]
    [InlineData(4, 1, false)]
    public async Task CombinedViewMenuMapsToProtocolWithoutStartingIo(int choice, int protocolIndex, bool monitorSelected)
    {
        var service = new FakeMonitor();
        var monitor = Model(service);
        await using var analyzer = new ProtocolAnalyzer();
        var protocol = new ProtocolViewModel(analyzer) { Modbus = monitor };
        var publishedChoices = new List<int>();
        protocol.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProtocolViewModel.SelectedViewIndex)) publishedChoices.Add(protocol.SelectedViewIndex);
        };

        protocol.SelectedViewIndex = choice;

        Assert.Equal(choice, protocol.SelectedViewIndex);
        Assert.Equal(protocolIndex, protocol.SelectedProtocolIndex);
        Assert.Equal(monitorSelected, protocol.IsRegisterMonitorSelected);
        Assert.Equal(choice != 0, analyzer.Enabled);
        Assert.All(publishedChoices, published => Assert.Equal(choice, published));
        Assert.Empty(service.Reads);
        Assert.Empty(service.Writes);
        Assert.False(monitor.IsBusy);
        protocol.SelectedViewIndex = -1;
        Assert.Equal(choice, protocol.SelectedViewIndex);
        protocol.SelectedViewIndex = 5;
        Assert.Equal(choice, protocol.SelectedViewIndex);
    }

    [Fact]
    public async Task CombinedViewRejectsUnavailableProtocolAndKeepsCurrentMonitor()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        var protocol = new ProtocolViewModel(analyzer) { SelectedViewIndex = 2 };
        protocol.SelectedViewIndex = 3;
        Assert.Equal(2, protocol.SelectedViewIndex);
        Assert.Equal(1, protocol.SelectedProtocolIndex);
        Assert.True(protocol.IsRegisterMonitorSelected);
        Assert.True(analyzer.Enabled);
        Assert.Contains("BACnet", protocol.Status);
    }

    [Fact]
    public async Task PresentationOptionsHaveStableLabelsBeforeSelectingAView()
    {
        var service = new FakeMonitor();
        await using var analyzer = new ProtocolAnalyzer();
        var protocol = new ProtocolViewModel(analyzer) { Modbus = Model(service) };
        var options = protocol.PresentationOptions;
        Assert.Equal(new[] { protocol.DisabledLabel, protocol.ModbusPacketViewLabel,
            protocol.ModbusReadWriteViewLabel, "Modbus RTU · 장치 시뮬레이터", protocol.MstpPacketViewLabel }, options.Select(option => option.Label));
        Assert.All(options, option => Assert.False(string.IsNullOrWhiteSpace(option.Label)));

        protocol.SelectedViewIndex = 2;
        protocol.IsVisible = true;
        protocol.Refresh(force: true);
        protocol.SelectedViewIndex = 1;
        Assert.Same(options, protocol.PresentationOptions);
        Assert.All(options, option => Assert.True(option.IsEnabled));
        Assert.Empty(service.Reads);
        Assert.Empty(service.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentationOptionsFollowBusyStateWithoutReplacingTheMenu(bool createWhileBusy)
    {
        var service = new FakeMonitor { HoldRun = true };
        var monitor = Model(service);
        await using var analyzer = new ProtocolAnalyzer();
        var protocol = new ProtocolViewModel(analyzer) { Modbus = monitor, SelectedViewIndex = 2 };
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<(int Index, bool Enabled)>();
        IReadOnlyList<ProtocolViewOption>? options = createWhileBusy ? null : protocol.PresentationOptions;
        if (options is not null) Observe(options);
        var run = monitor.RunAsync(true);
        try
        {
            monitor.Refresh();
            if (options is null) { options = protocol.PresentationOptions; Observe(options); }
            Assert.True(monitor.IsBusy);
            Assert.Equal(new[] { false, false, false, false, false }, options.Select(option => option.IsEnabled));
            Assert.Same(options, protocol.PresentationOptions);
            if (!createWhileBusy)
            {
                Assert.Contains((0, false), notifications);
                Assert.Contains((3, false), notifications);
            }
            protocol.SelectedViewIndex = 0;
            Assert.Equal(2, protocol.SelectedViewIndex);
            protocol.SelectedViewIndex = 3;
            Assert.Equal(2, protocol.SelectedViewIndex);
            protocol.SelectedViewIndex = 1;
            Assert.Equal(2, protocol.SelectedViewIndex);
            Assert.True(monitor.IsBusy);
        }
        finally { await monitor.StopAsync(); await run; }
        Assert.Same(options, protocol.PresentationOptions);
        Assert.All(options!, option => Assert.True(option.IsEnabled));
        Assert.Contains((0, true), notifications);
        Assert.Contains((3, true), notifications);
        Assert.Single(service.Reads);
        Assert.Empty(service.Writes);

        void Observe(IReadOnlyList<ProtocolViewOption> observed)
        {
            foreach (var (option, index) in observed.Select((option, index) => (option, index)))
                option.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ProtocolViewOption.IsEnabled))
                        notifications.Enqueue((index, option.IsEnabled));
                };
        }
    }

    [Fact]
    public async Task SharedHeaderFollowsMonitorTargetAndUnsubscribesReplacedModel()
    {
        await using var analyzer = new ProtocolAnalyzer();
        var first = Model(new FakeMonitor());
        var protocol = new ProtocolViewModel(analyzer) { Modbus = first, SelectedViewIndex = 2 };
        var changed = new List<string?>();
        protocol.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        first.AddressText = "123";
        Assert.Contains(nameof(ProtocolViewModel.WorkspaceHeader), changed);
        Assert.Contains(first.RequestSummary, protocol.WorkspaceHeader);

        var second = Model(new FakeMonitor());
        second.AddressText = "456";
        protocol.Modbus = second;
        Assert.Contains(second.RequestSummary, protocol.WorkspaceHeader);
        changed.Clear();
        first.AddressText = "789";
        Assert.DoesNotContain(nameof(ProtocolViewModel.WorkspaceHeader), changed);
        protocol.IsRegisterMonitorSelected = false;
        Assert.Equal(1, protocol.SelectedViewIndex);
        protocol.SelectedProtocolIndex = 2;
        Assert.Equal(3, protocol.SelectedViewIndex);
    }

    [Fact]
    public async Task ConflictingOperationAndFailedSaveSurfaceErrorsWithoutSending()
    {
        var service = new FakeMonitor();
        var model = new ModbusRtuViewModel(service, () => false, () => throw new IOException("disk failure"), () => { });
        await model.RunAsync(false);
        Assert.Empty(service.Reads);
        Assert.NotEmpty(model.Error);
        await model.SaveSettingsAsync();
        Assert.Contains("disk failure", model.Error);
        model.QuantityText = "126";
        var saved = 0;
        var invalidModel = new ModbusRtuViewModel(service, () => true, () => { saved++; return Task.CompletedTask; }, () => { }) { QuantityText = "126" };
        await invalidModel.SaveSettingsAsync();
        Assert.Equal(0, saved);
    }

    [Fact]
    public void UnavailableStartExplainsHowToResolveTheBlockedAction()
    {
        var available = false;
        var model = new ModbusRtuViewModel(new FakeMonitor(), () => available, () => Task.CompletedTask, () => { }, () => "Turn off terminal direct input.");
        Assert.False(model.StartCommand.CanExecute(null));
        Assert.Equal(Visibility.Visible, model.AvailabilityVisibility);
        Assert.Contains("direct input", model.AvailabilityText);
        available = true;
        model.RefreshAvailability();
        Assert.True(model.StartCommand.CanExecute(null));
        Assert.Equal(Visibility.Collapsed, model.AvailabilityVisibility);
    }

    [Fact]
    public void SavedSettingsAreDetachedAndIncludeDisplayConfiguration()
    {
        var service = new FakeMonitor();
        var model = Model(service);
        var original = new ModbusMonitorSettings { UnitId = 7, Address = 21, Quantity = 4, IntervalMs = 1500, TimeoutMs = 2500,
            ValueType = ModbusValueType.Float32, Scale = 0.1, SwapBytes = true, SwapWords = true };
        model.ApplySettings(original);
        original.Address = 100;
        var saved = model.CreateSettingsSnapshot();
        Assert.Equal(21, saved.Address);
        Assert.Equal(7, saved.UnitId);
        Assert.Equal(1500, saved.IntervalMs);
        Assert.Equal(2500, saved.TimeoutMs);
        Assert.Equal(ModbusValueType.Float32, saved.ValueType);
        Assert.Equal(0.1, saved.Scale);
        Assert.True(saved.SwapBytes && saved.SwapWords);
        saved.Address = 200;
        Assert.Equal("21", model.AddressText);
        Assert.Empty(service.Reads);
    }

    [Fact]
    public void InvalidDraftDoesNotBreakGenericProfileSaveOrReplaceTheSavedTarget()
    {
        var model = Model(new FakeMonitor());
        model.ApplySettings(new ModbusMonitorSettings { UnitId = 7, Address = 40 });
        model.AddressText = "";
        Assert.Throws<ArgumentException>(() => model.CreateSettingsSnapshot());
        var persisted = model.CreatePersistableSettingsSnapshot();
        Assert.Equal(7, persisted.UnitId);
        Assert.Equal(40, persisted.Address);
        persisted.Address = 100;
        Assert.Equal(40, model.CreatePersistableSettingsSnapshot().Address);
        model.ApplySettings(new ModbusMonitorSettings { UnitId = 0, Address = 65536 });
        persisted = model.CreatePersistableSettingsSnapshot();
        Assert.Equal(0, persisted.UnitId);
        Assert.Equal(65536, persisted.Address);
    }

    [Fact]
    public async Task ToolbarSummaryKeepsErrorsAndConnectionRestrictionsAheadOfReadStatistics()
    {
        var service = new FakeMonitor();
        var available = true;
        var model = new ModbusRtuViewModel(service, () => available, () => Task.CompletedTask, () => { }, () => "Sequence owns this connection");
        Assert.Equal(model.MonitorDetailsLabel, model.MonitorSummary);
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        await model.RunAsync(false);
        Assert.NotEmpty(model.Statistics);
        Assert.Equal(model.Statistics, model.MonitorSummary);
        available = false;
        model.RefreshAvailability();
        Assert.Equal("Sequence owns this connection", model.MonitorSummary);
        available = true;
        model.AddressText = "65536";
        await model.RunAsync(false);
        Assert.NotEmpty(model.Error);
        available = false;
        model.RefreshAvailability();
        Assert.Equal(model.Error, model.MonitorSummary);
        Assert.Contains(nameof(model.MonitorSummary), changed);
        Assert.Single(service.Reads);
        Assert.Empty(service.Writes);
        available = true;
        model.AddressText = "0";
        await model.RunAsync(false);
        Assert.Empty(model.Error);
        Assert.Equal(model.Statistics, model.MonitorSummary);
    }

    [Fact]
    public void SourceLayoutKeepsSharedStopOutsideEditorsAndUsesOneViewMenu()
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var monitor = Markup("Views/ModbusRtuView.xaml");
        var workspace = Assert.Single(monitor.Root!.Elements(p + "Grid"));
        Assert.Equal("Auto,*", (string?)workspace.Attribute("RowDefinitions"));
        var toolbar = workspace.Elements(p + "Border").First().Element(p + "Grid")!;
        Assert.Null(toolbar.Attribute("RowDefinitions"));
        Assert.Contains(toolbar.Elements(), element => (string?)element.Attribute(x + "Name") == "PollingGroupSelector");
        Assert.All(monitor.Descendants(p + "TextBlock").Where(element =>
            (string?)element.Attribute("Text") == "{Binding RecentCommandCaption}"), result =>
            Assert.Contains(result.Ancestors(), parent => parent.Name == p + "Flyout"));
        Assert.All(monitor.Descendants(p + "Expander"), detail =>
            Assert.Contains(detail.Ancestors(), parent => parent.Name == p + "Flyout"));
        var settingsPanel = Assert.Single(monitor.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "SettingsPanel"));
        Assert.Contains(settingsPanel.Descendants(p + "TextBlock"), label =>
            (string?)label.Attribute("Text") == "{Binding TargetSettingsSummary}");
        Assert.DoesNotContain(settingsPanel.Descendants(p + "TextBlock"), label =>
            (string?)label.Attribute("Text") == "{Binding RequestSummary}");
        Assert.All(monitor.Descendants(p + "TextBox"), input =>
        {
            Assert.Null(input.Attribute("MaxLength"));
            Assert.NotNull(input.Attribute("AutomationProperties.Name"));
        });
        var protocol = Markup("Views/ProtocolView.xaml");
        Assert.Empty(protocol.Descendants(p + "RadioButton"));
        var selector = Assert.Single(protocol.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "PresentationSelector"));
        Assert.Equal("{Binding SelectedPresentationIndex, Mode=TwoWay}", (string?)selector.Attribute("SelectedIndex"));
        Assert.Equal("{Binding PresentationOptions}", (string?)selector.Attribute("ItemsSource"));
        Assert.Equal("Label", (string?)selector.Attribute("DisplayMemberPath"));
        var activeStop = Assert.Single(protocol.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "MonitorStopButton"));
        Assert.Equal("{Binding StopCommand}", (string?)activeStop.Attribute("Command"));
        Assert.DoesNotContain(activeStop.Ancestors(), e => e.Name == p + "ScrollViewer" || e.Name == p + "Expander" || e.Name == p + "Flyout");
        Assert.DoesNotContain(activeStop.Ancestors(), e => (string?)e.Attribute("Visibility") == "{Binding PacketAnalysisVisibility}" || (string?)e.Attribute("Visibility") == "{Binding RegisterMonitorVisibility}");
        Assert.DoesNotContain(Markup("MainWindow.xaml").Descendants(p + "TabViewItem"), tab => tab.Descendants().Any(e => e.Name.LocalName == "ModbusRtuView"));
    }

    private static ModbusRtuViewModel Model(FakeMonitor service) => new(service, () => true, () => Task.CompletedTask, () => { });
    private static XDocument Markup(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "SerialMonitor.WinUI", relative);
            if (File.Exists(path)) return XDocument.Load(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relative);
    }
    private sealed class FakeMonitor : IModbusRtuMonitorService
    {
        private ModbusMonitorSnapshot _snapshot = new(0, ModbusMonitorState.Idle, false, false, null, null, 0, 0, null, null, false);
        private readonly TaskCompletionSource _run = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsBusy => _snapshot.IsBusy;
        public bool HoldRun { get; init; }
        public ushort[]? Values { get; init; }
        public Action? BeforeWrite { get; set; }
        public List<(ModbusMonitorSettings Settings, bool Repeat)> Reads { get; } = [];
        public List<(int Address, ushort[] Values)> Writes { get; } = [];
        public ModbusMonitorSnapshot GetSnapshot() => _snapshot;
        public async Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken)
        {
            Reads.Add((settings.Clone(), repeat));
            _snapshot = new(_snapshot.Revision + 1, repeat ? ModbusMonitorState.Polling : ModbusMonitorState.Idle,
                HoldRun, HoldRun && repeat, Values ?? new ushort[settings.Quantity], DateTimeOffset.Now, 1, 0, 2, null, true);
            if (HoldRun) await _run.Task.WaitAsync(cancellationToken);
        }
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        {
            BeforeWrite?.Invoke();
            Writes.Add((address, values.ToArray()));
            _snapshot = _snapshot with { Revision = _snapshot.Revision + 1, WriteState = ModbusWriteState.Verified };
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            _snapshot = _snapshot with { Revision = _snapshot.Revision + 1, State = ModbusMonitorState.Stopped, IsBusy = false, IsPolling = false, ValuesCurrent = false };
            _run.TrySetResult();
            return Task.CompletedTask;
        }
        public void SetValues(ushort[] values, bool current) => _snapshot = _snapshot with { Revision = _snapshot.Revision + 1, Values = values, ValuesCurrent = current };
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
