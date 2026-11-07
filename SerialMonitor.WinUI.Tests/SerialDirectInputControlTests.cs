using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialDirectInputControlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADriverChangeDoesNotDisableOrReplaceTheTerminalInputSession(bool configuration)
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        void Set(string name, object value) => typeof(MainViewModel).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);
        await using var bridge = new SerialBridgeService();
        await using var session = new TerminalInputSession((_, _, _) => Task.CompletedTask, _ => { });
        Set("_automationControl", new AutomationControlGate());
        Set("_viewPauseGate", new object());
        var pauseType = typeof(MainViewModel).GetField("_viewPause", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType;
        Set("_viewPause", Activator.CreateInstance(pauseType)!);
        Set("_bridgeService", bridge);
        Set("_currentUiSettings", new UiSettings());
        Set("_currentSerialSettings", new SerialSettings());
        Set("_isConnected", true);
        Set("_isXtermReady", true);
        Set("_terminalInputSession", session);
        Set("_serialControlLineStatus", new SerialControlLineStatus(1, SerialControlLineMode.Off,
            SerialControlLineMode.Off, SerialModemStatusKind.Available));
        Assert.True(model.IsTerminalDirectInputEnabled);
        Assert.True(model.CanEditDtrControl);
        Assert.True(model.CanEditRtsControl);
        Set(configuration ? "_serialConfigurationBusy" : "_serialControlLineBusy", true);
        Assert.True(model.CanEnableTerminalDirectInput);
        typeof(MainViewModel).GetMethod("OnTerminalInputStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [model, new PropertyChangedEventArgs(nameof(MainViewModel.CanSendManualTx))]);
        Assert.True(model.IsTerminalDirectInputEnabled);
        Assert.True(session.TryEnqueue("still active"));
    }
}
