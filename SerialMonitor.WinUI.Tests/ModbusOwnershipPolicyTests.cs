using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusOwnershipPolicyTests
{
    [Fact]
    public async Task ModbusDisablesEveryCompetingGuiOperationButKeepsDisconnectAvailable()
    {
        await using var fixture = await Fixture.Create();
        var model = fixture.Model;
        Assert.True(model.CanSendManualTx);
        Assert.True(CanRun(model));
        Assert.True(model.CanStartBridge);
        Assert.True(model.CanEnableTerminalDirectInput);
        Assert.True(model.CanEditSerialParameters);
        Assert.True(model.CanEditDtrControl);
        Assert.True(model.CanEditRtsControl);
        fixture.Monitor.Busy = true;
        Assert.True(model.IsModbusBusy);
        Assert.False(model.CanSendManualTx);
        Assert.False(CanRun(model));
        Assert.False(model.CanStartBridge);
        Assert.False(model.CanEnableTerminalDirectInput);
        Assert.False(model.CanEditSerialParameters);
        Assert.False(model.CanEditDtrControl);
        Assert.False(model.CanEditRtsControl);
        Assert.False(model.CanEditRtsToggle);
        Assert.False(model.CanChangeTerminalMode);
        Assert.True(model.CanManualDisconnect);
        // A stale transport disconnect must not enable configuration or reconnect
        // while a Modbus owner is still finishing its response window/cleanup.
        Set(model, "_isConnected", false);
        Assert.False(model.CanConnect);
        Assert.False(model.CanEditConnectionSettings);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("configure")]
    [InlineData("set_control_line")]
    public async Task ModbusRejectsAutomationReservationBeforeAnyOperationIsExecuted(string operation)
    {
        await using var fixture = await Fixture.Create();
        fixture.Monitor.Busy = true;
        var args = JsonSerializer.SerializeToElement(new { operation });
        var error = await Assert.ThrowsAsync<AutomationException>(() =>
            (Task<object>)Invoke(fixture.Model, "InvokeAutomationOnUiAsync", "reserve", args, CancellationToken.None)!);
        Assert.Equal("busy", error.Code);
        Assert.False(fixture.Control.IsAutomationReserved);
        Assert.Equal(0, fixture.Serial.WrittenByteCount);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("automation")]
    [InlineData("bridge-start")]
    [InlineData("manual-send")]
    [InlineData("configuration")]
    [InlineData("control-line")]
    [InlineData("profile-save")]
    [InlineData("reconnect")]
    [InlineData("terminal-input")]
    public async Task ExistingOwnersRejectModbusAdmission(string owner)
    {
        await using var fixture = await Fixture.Create();
        Assert.True((bool)Invoke(fixture.Model, "CanStartModbusOperation")!);
        using var bridgeStart = owner == "bridge-start" ? fixture.Control.TryBeginBridgeStart() : null;
        await using var terminal = new TerminalInputSession((_, _, _) => Task.CompletedTask, _ => { });
        switch (owner)
        {
            case "sequence": Set(fixture.Model, "_isSequenceRunning", true); break;
            case "automation": Assert.True(fixture.Control.TryReserveAutomation()); break;
            case "manual-send": Set(fixture.Model, "_activeGuiSends", 1); break;
            case "configuration": Set(fixture.Model, "_serialConfigurationBusy", true); break;
            case "control-line": Set(fixture.Model, "_serialControlLineBusy", true); break;
            case "profile-save": Set(fixture.Model, "_isProfileSaveActive", true); break;
            case "reconnect": Set(fixture.Model, "_isAutoReconnectRunning", true); break;
            case "terminal-input": Set(fixture.Model, "_terminalInputSession", terminal); break;
        }
        Assert.False((bool)Invoke(fixture.Model, "CanStartModbusOperation")!);
        Assert.Equal(0, fixture.Serial.WrittenByteCount);
    }

    [Fact]
    public async Task ModbusFailureCannotArmAnAutomaticReconnectWhileOwnershipIsRetained()
    {
        await using var fixture = await Fixture.Create();
        fixture.Monitor.Busy = true;
        Set(fixture.Model, "_autoReconnectArmed", 1);
        Invoke(fixture.Model, "TryStartAutoReconnect");
        Assert.False(fixture.Model.IsAutoReconnectRunning);
        Assert.Null(Get(fixture.Model, "_autoReconnectTask"));
    }

    private static bool CanRun(MainViewModel model) => (bool)Invoke(model, "CanRunCommandSequence", (object?)null)!;
    private static object? Invoke(MainViewModel model, string name, params object?[] args) =>
        typeof(MainViewModel).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, args);
    private static object? Get(MainViewModel model, string name) =>
        typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model);
    private static void Set(MainViewModel model, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, value);

    private sealed class Fixture(MainViewModel model, SerialService serial, SerialBridgeService bridge,
        MonitorStub monitor, AutomationControlGate control) : IAsyncDisposable
    {
        public MainViewModel Model { get; } = model;
        public SerialService Serial { get; } = serial;
        public MonitorStub Monitor { get; } = monitor;
        public AutomationControlGate Control { get; } = control;

        public static async Task<Fixture> Create()
        {
            var serial = new SerialService();
            await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
            var bridge = new SerialBridgeService();
            var control = new AutomationControlGate();
            var monitor = new MonitorStub();
            var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            Set(model, "_serialService", serial);
            Set(model, "_bridgeService", bridge);
            Set(model, "_automationControl", control);
            Set(model, "_currentSerialSettings", new SerialSettings { PortName = "MOCK" });
            Set(model, "_currentUiSettings", new UiSettings());
            Set(model, "_isConnected", true);
            Set(model, "_isXtermReady", true);
            Set(model, "_selectedPort", "COM7");
            Set(model, "_selectedBridgePort", "COM8");
            Set(model, "_startupProfileTask", Task.CompletedTask);
            Set(model, "_terminalInputCleanup", Task.CompletedTask);
            Set(model, "_selectedCommandSequence", new CommandSequence { Name = "test", Steps = [new() { CommandText = "ping" }] });
            Set(model, "_serialControlLineStatus", new SerialControlLineStatus(serial.ReceiveSessionGeneration,
                SerialControlLineMode.Off, SerialControlLineMode.Off, SerialModemStatusKind.Mock));
            Set(model, "_viewPauseGate", new object());
            Set(model, "_viewPause", new ViewPauseStateMachine());
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.Modbus))!.SetValue(model,
                new ModbusRtuViewModel(monitor, () => false, () => Task.CompletedTask, () => { }));
            return new(model, serial, bridge, monitor, control);
        }

        public async ValueTask DisposeAsync()
        {
            await bridge.DisposeAsync();
            await Serial.DisposeAsync();
        }
    }

    private sealed class MonitorStub : IModbusRtuMonitorService
    {
        public bool Busy;
        public bool IsBusy => Busy;
        public ModbusMonitorSnapshot GetSnapshot() => new(0, ModbusMonitorState.Polling, Busy, Busy,
            null, null, 0, 0, null, null, false);
        public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
