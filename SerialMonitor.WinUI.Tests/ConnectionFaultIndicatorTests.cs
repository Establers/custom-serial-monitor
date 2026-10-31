using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ConnectionFaultIndicatorTests
{
    [Fact]
    public async Task SerialFaultStaysVisibleThroughRetryCleanupAndClearsOnSuccessfulConnection()
    {
        await using var serial = new SerialService();
        await using var bridge = new SerialBridgeService();
        var model = ObserveSerialStatus(serial, bridge);
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        Assert.False(model.IsSerialConnectionFaulted);

        Invoke(serial, "ReportError", "Injected transport failure", true, SerialConnectionState.Faulted);
        Assert.True(model.IsSerialConnectionFaulted);
        await serial.DisconnectAsync(default);
        Assert.Equal(SerialConnectionState.Disconnected, serial.ConnectionState);
        Assert.True(model.IsSerialConnectionFaulted);
        Invoke(serial, "SetConnectionState", SerialConnectionState.Connecting, false);
        Assert.True(model.IsSerialConnectionFaulted);
        Invoke(serial, "ReportError", "Retry failed", true, SerialConnectionState.Faulted);
        Assert.True(model.IsSerialConnectionFaulted);

        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        Assert.False(model.IsSerialConnectionFaulted);
        await serial.DisconnectAsync(default);
        Assert.False(model.IsSerialConnectionFaulted);
    }

    [Fact]
    public async Task QuietPortAndNonfatalSerialWarningsDoNotSetTheFaultIndicator()
    {
        await using var serial = new SerialService();
        await using var bridge = new SerialBridgeService();
        var model = ObserveSerialStatus(serial, bridge);
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        Invoke(serial, "ReportError", "Nonfatal port scan warning", false, null);
        Assert.True(serial.IsConnected);
        Assert.False(model.IsSerialConnectionFaulted);
        await serial.DisconnectAsync(default);
        Assert.False(model.IsSerialConnectionFaulted);
    }

    internal static MainViewModel ObserveSerialStatus(SerialService serial, SerialBridgeService bridge)
    {
        // Exercise the production serial-status handler without creating a desktop.
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        SetField(model, "_serialService", serial);
        SetField(model, "_bridgeService", bridge);
        SetField(model, "_backgroundHealthErrorGate", new object());
        SetField(model, "_activeBackgroundHealthErrors", new Dictionary<string, string>());
        serial.StatusChanged += (sender, args) => Invoke(model, "OnSerialStatusChanged", sender, args);
        return model;
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static void Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
}
