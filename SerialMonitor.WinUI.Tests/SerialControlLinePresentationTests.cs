using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialControlLinePresentationTests
{
    [Fact]
    public void DriverStateIsDisplayedSeparatelyFromTheSavedRequestedSettings()
    {
        var model = Create(new(3, SerialControlLineMode.On, SerialControlLineMode.Off,
            SerialModemStatusKind.Available, true, false, true, false));
        Assert.True(model.DtrEnable); // Saved configuration is OFF.
        Assert.False(model.RtsEnable); // Saved configuration is ON.
        Assert.Equal("DTR ON", model.DtrControlText);
        Assert.Equal("RTS OFF", model.RtsControlText);
        Assert.True(model.DtrControlChecked);
        Assert.False(model.RtsControlChecked);
        Assert.Equal("CTS ON", model.CtsStatusText);
        Assert.Equal("DSR OFF", model.DsrStatusText);
        Assert.Equal("DCD ON", model.DcdStatusText);
        Assert.Equal("RI OFF", model.RiStatusText);
    }

    [Theory]
    [InlineData(SerialModemStatusKind.Unsupported)]
    [InlineData(SerialModemStatusKind.Error)]
    [InlineData(SerialModemStatusKind.Mock)]
    public void UnavailableInputsAreNeverPresentedAsOff(SerialModemStatusKind kind)
    {
        var model = Create(new(3, SerialControlLineMode.Off, SerialControlLineMode.Off, kind));
        Assert.Equal("CTS N/A", model.CtsStatusText);
        Assert.Equal("DSR N/A", model.DsrStatusText);
        Assert.Equal("DCD N/A", model.DcdStatusText);
        Assert.Equal("RI N/A", model.RiStatusText);
    }

    [Fact]
    public void DisconnectedInputsUseAnExplicitDisconnectedState()
    {
        var model = Create(SerialControlLineStatus.Disconnected, connected: false);
        Assert.Equal("CTS —", model.CtsStatusText);
        Assert.Equal("RI —", model.RiStatusText);
        Assert.True(model.CanEditDtrControl);
        Assert.True(model.CanEditRtsControl);
    }

    [Theory]
    [InlineData(SerialControlLineMode.Handshake, false, false, "RTS AUTO")]
    [InlineData(SerialControlLineMode.Toggle, false, true, "RTS TX")]
    [InlineData(SerialControlLineMode.Unknown, false, false, "RTS N/A")]
    [InlineData(SerialControlLineMode.On, true, true, "RTS ON")]
    public void OnlyManualOutputsCanBeToggled(SerialControlLineMode mode, bool canManual, bool canSelectMode, string caption)
    {
        var model = Create(new(3, SerialControlLineMode.Handshake, mode, SerialModemStatusKind.Available));
        Assert.False(model.CanEditDtrControl);
        Assert.Equal(canManual, model.CanEditRtsControl);
        Assert.Equal(canSelectMode, model.CanEditRtsToggle);
        Assert.Equal(caption, model.RtsControlText);
        Assert.Null(model.DtrControlChecked);
        if (!SerialControlLinePolicy.IsManual(mode)) Assert.Null(model.RtsControlChecked);
    }

    [Fact]
    public void AutomationReservationLocksAllGuiOutputChanges()
    {
        var model = Create(new(3, SerialControlLineMode.Off, SerialControlLineMode.Off, SerialModemStatusKind.Available));
        var gate = (AutomationControlGate)typeof(MainViewModel).GetField("_automationControl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        Assert.True(gate.TryReserveAutomation());
        Assert.False(model.CanEditDtrControl);
        Assert.False(model.CanEditRtsControl);
        Assert.False(model.CanEditRtsToggle);
    }

    private static MainViewModel Create(SerialControlLineStatus status, bool connected = true)
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);
        Set("_automationControl", new AutomationControlGate());
        Set("_currentSerialSettings", new SerialSettings { RtsEnable = true });
        Set("_serialControlLineStatus", status);
        Set("_isConnected", connected);
        return model;
    }
}
