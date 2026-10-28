using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogDestinationSettingsTests
{
    [Theory]
    [InlineData(false, false, false, false, 0, true)]
    [InlineData(true, false, false, false, 0, true)]
    [InlineData(false, true, false, false, 0, false)]
    [InlineData(true, true, false, false, 0, false)]
    [InlineData(true, false, true, false, 0, false)]
    [InlineData(true, false, false, true, 0, false)]
    [InlineData(true, false, false, false, 1, false)]
    public void FolderEditing_DependsOnLoggingStateAndNotSerialConnection(
        bool connected, bool logging, bool busy, bool validating, int transitions, bool expected)
    {
        var model = CreateState();
        SetField(model, "_currentLogSettings", new LogSettings { FileLoggingEnabled = logging });
        SetField(model, "_isConnected", connected);
        SetField(model, "_isBusy", busy);
        SetField(model, "_isLogSaveDirectoryValidating", validating);
        SetField(model, "_fileLoggingTransitionCount", transitions);

        Assert.Equal(expected, model.CanEditLogSaveDirectory);
    }

    [Fact]
    public void LoggingStart_ClearsOnlyLogDestinationPendingSettings()
    {
        var model = CreateState();

        Invoke(model, "ClearPendingLogStartSettings");

        Assert.Equal(0, model.PendingLogStartSettingsCount);
        Assert.Equal(1, model.PendingReconnectRequiredSettingsCount);
        Assert.Equal(1, model.PendingRestartRequiredSettingsCount);
    }

    [Fact]
    public void ProfileReplacement_DismissesPendingChangesFromThePreviousProfile()
    {
        var model = CreateState();

        Invoke(model, "ClearPendingSettings");

        Assert.Equal(0, model.PendingLogStartSettingsCount);
        Assert.Equal(0, model.PendingReconnectRequiredSettingsCount);
        Assert.Equal(0, model.PendingRestartRequiredSettingsCount);
    }

    [Fact]
    public async Task ValidatedFolder_UpdatesBothSavedPathsWithoutChangingConnection()
    {
        using var cancellation = new CancellationTokenSource();
        var model = CreateValidationState(new LogDirectoryValidator(path => path), cancellation);

        Assert.True(await model.SetLogSaveDirectoryAsync("new folder"));

        Assert.Equal("new folder", model.LogSaveDirectory);
        var serialSettings = (SerialSettings)typeof(MainViewModel)
            .GetField("_currentSerialSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        Assert.Equal("new folder", serialSettings.SaveDirectory);
        Assert.True(model.IsConnected);
        Assert.True(model.CanEditLogSaveDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingFolderCheck_DoesNotApplyAfterProfileReplacementOrShutdown(bool shutdown)
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validator = new LogDirectoryValidator(path =>
        {
            entered.TrySetResult();
            try { release.Wait(TimeSpan.FromSeconds(5)); return path; }
            finally { finished.TrySetResult(); }
        });
        var model = CreateValidationState(validator, cancellation);
        try
        {
            var check = model.SetLogSaveDirectoryAsync("obsolete folder");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(model.CanEditLogSaveDirectory);
            if (shutdown)
            {
                cancellation.Cancel();
            }
            else
            {
                SetField(model, "_logDestinationVersion", 1L);
                SetField(model, "_currentLogSettings", new LogSettings { SaveDirectory = "profile folder" });
                release.Set();
            }

            Assert.False(await check.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(shutdown ? "original folder" : "profile folder", model.LogSaveDirectory);
            Assert.Equal(0, model.SettingsValidationErrorCount);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task RestartingLogging_ChangesFolderWhileSerialConnectionStaysActive()
    {
        var root = Path.Combine(Path.GetTempPath(), "SerialMonitor.DestinationTests", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var serial = new SerialService();
            await using var writer = new FileLogWriter();
            await serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new(), cancellation.Token);
            var generation = serial.ReceiveSessionGeneration;
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            await writer.StartAsync(first, cancellation.Token);
            Assert.True(writer.TryEnqueue(LogLine.Rx("before folder change")));
            await writer.StopAsync(cancellation.Token);
            var firstPath = writer.LastLogFilePath!;

            await writer.StartAsync(second, cancellation.Token);
            Assert.True(writer.TryEnqueue(LogLine.Rx("after folder change")));
            await writer.StopAsync(cancellation.Token);
            var secondPath = writer.LastLogFilePath!;

            Assert.True(serial.IsConnected);
            Assert.Equal(generation, serial.ReceiveSessionGeneration);
            Assert.Equal(first, Path.GetDirectoryName(firstPath));
            Assert.Equal(second, Path.GetDirectoryName(secondPath));
            Assert.Contains("before folder change", await File.ReadAllTextAsync(firstPath, cancellation.Token));
            Assert.Contains("after folder change", await File.ReadAllTextAsync(secondPath, cancellation.Token));
            Assert.DoesNotContain("after folder change", await File.ReadAllTextAsync(firstPath, cancellation.Token));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static MainViewModel CreateState()
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        SetField(model, "_pendingReconnectSettings", new HashSet<string> { "Baudrate" });
        SetField(model, "_pendingRestartSettings", new HashSet<string> { "App setting" });
        SetField(model, "_pendingLogStartSettings", new HashSet<string> { "Log save directory", "Log file name" });
        return model;
    }

    private static MainViewModel CreateValidationState(LogDirectoryValidator validator, CancellationTokenSource cancellation)
    {
        var model = CreateState();
        SetField(model, "_currentLogSettings", new LogSettings { SaveDirectory = "original folder" });
        SetField(model, "_currentSerialSettings", new SerialSettings { SaveDirectory = "original folder" });
        SetField(model, "_isConnected", true);
        SetField(model, "_suppressSettingsApplyRecording", true);
        SetField(model, "_logDirectoryValidator", validator);
        SetField(model, "_settingsValidationCancellation", cancellation);
        foreach (var name in new[] { "ToggleFileLoggingCommand", "ConnectCommand", "ToggleConnectionCommand", "SaveProfileCommand" })
            SetField(model, $"<{name}>k__BackingField", new AsyncRelayCommand(() => Task.CompletedTask));
        return model;
    }

    private static void SetField(MainViewModel model, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);

    private static void Invoke(MainViewModel model, string name) =>
        typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
}
