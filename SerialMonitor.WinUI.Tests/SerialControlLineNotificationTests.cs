using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialControlLineNotificationTests
{
    [Fact]
    public void PollingUnchangedDriverValuesDoesNotNotifyAgain()
    {
        var (model, driver) = Create();
        var changes = Observe(model);
        Refresh(model);
        Assert.Contains(nameof(model.CtsStatusText), changes);
        changes.Clear();

        // Drivers can publish a new record with identical values every poll.
        driver.Status = driver.Status with { };
        Refresh(model);
        Assert.Empty(changes);
    }

    [Fact]
    public void ChangedDriverInputsAndOutputModesStillReachBindings()
    {
        var (model, driver) = Create();
        Refresh(model);
        var changes = Observe(model);
        driver.Status = driver.Status with { Cts = true, Rts = SerialControlLineMode.Toggle };
        Refresh(model);
        Assert.Contains(nameof(model.CtsStatusText), changes);
        Assert.Contains(nameof(model.RtsControlText), changes);
        Assert.Equal("CTS ON", model.CtsStatusText);
        Assert.Equal("RTS TX", model.RtsControlText);
        Assert.False(model.CanEditRtsControl);
    }

    [Fact]
    public void ReservationAndBusyChangesNotifyWithoutAnyDriverChange()
    {
        var (model, _) = Create();
        Refresh(model);
        var changes = Observe(model);
        var gate = (AutomationControlGate)Get(model, "_automationControl")!;
        Assert.True(gate.TryReserveAutomation());
        Refresh(model);
        Assert.Contains(nameof(model.CanEditDtrControl), changes);
        Assert.False(model.CanEditDtrControl);

        changes.Clear();
        Set(model, "_serialControlLineBusy", true);
        Refresh(model);
        Assert.Contains(nameof(model.IsSerialControlLineBusy), changes);
        Assert.True(model.IsSerialControlLineBusy);
    }

    [Fact]
    public void DisconnectedHandshakeChangesStillUpdateOutputPresentation()
    {
        var (model, _) = Create();
        Set(model, "_isConnected", false);
        Refresh(model);
        var changes = Observe(model);
        ((SerialSettings)Get(model, "_currentSerialSettings")!).Handshake = SerialHandshakeMode.DtrRts;
        Refresh(model);
        Assert.Contains(nameof(model.DtrControlText), changes);
        Assert.Equal("DTR AUTO", model.DtrControlText);
        Assert.Equal("RTS AUTO", model.RtsControlText);
        Assert.False(model.CanEditDtrControl);
    }

    [Fact]
    public void NewGenerationClearsTheOldErrorAndPublishesDisconnectedInputs()
    {
        var (model, driver) = Create();
        Set(model, "_serialControlLineErrorGeneration", driver.Status.Generation);
        Set(model, "_serialControlLineError", "Old session error");
        Refresh(model);
        Assert.Equal("Old session error", model.SerialControlLineHint);
        var changes = Observe(model);
        driver.Status = SerialControlLineStatus.Disconnected;
        Set(model, "_isConnected", false);
        Refresh(model);
        Assert.Contains(nameof(model.SerialControlLineHint), changes);
        Assert.Null(Get(model, "_serialControlLineError"));
        Assert.Equal("CTS —", model.CtsStatusText);
    }

    [Fact]
    public void ExplicitRefreshStillRestoresRejectedEditsWithUnchangedValues()
    {
        var (model, _) = Create();
        Refresh(model);
        var changes = Observe(model);
        Refresh(model, onlyIfChanged: false);
        Assert.Contains(nameof(model.DtrControlChecked), changes);
        Assert.Contains(nameof(model.RtsEnable), changes);
        changes.Clear();
        // Explicit refresh invalidates the last poll's baseline. The next poll
        // re-establishes it before unchanged notifications can be suppressed.
        Refresh(model);
        Assert.Contains(nameof(model.DtrControlChecked), changes);
        changes.Clear();
        Refresh(model);
        Assert.Empty(changes);
    }

    private static (MainViewModel Model, DriverState Driver) Create()
    {
        var serial = DispatchProxy.Create<ISerialService, DriverState>();
        var driver = (DriverState)(object)serial;
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(model, "_serialService", serial);
        Set(model, "_automationControl", new AutomationControlGate());
        Set(model, "_bridgeService", new SerialBridgeService()); // Never started; no COM access.
        Set(model, "_currentSerialSettings", new SerialSettings());
        Set(model, "_isConnected", true);
        return (model, driver);
    }

    private static List<string> Observe(MainViewModel model)
    {
        var changes = new List<string>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName!);
        return changes;
    }

    private static void Refresh(MainViewModel model, bool onlyIfChanged = true) =>
        typeof(MainViewModel).GetMethod("RefreshSerialControlLineProperties", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [onlyIfChanged]);
    private static object? Get(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public class DriverState : DispatchProxy
    {
        public SerialControlLineStatus Status { get; set; } = new(7,
            SerialControlLineMode.Off, SerialControlLineMode.Off, SerialModemStatusKind.Available,
            false, false, false, false);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_ControlLineStatus" ? Status : throw new NotSupportedException(targetMethod?.Name);
    }
}
