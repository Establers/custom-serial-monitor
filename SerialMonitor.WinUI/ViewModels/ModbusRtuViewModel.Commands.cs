using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusRtuViewModel
{
    private ModbusCommandSettings? _commandSettings;
    private ModbusCommandChoice? _selectedCommand;
    private bool _commandAdmitted, _acceptCommandSnapshots;
    private long _commandSettingsRevision, _lastCommandRevision = long.MinValue;
    private string _commandStatus = "", _commandResults = "";

    private static string C(string key, string fallback) => UiText.Get("ModbusCommand" + key, fallback);
    public string CommandsLabel => C("Actions", "Commands…");
    public string GuideCommandsTitle => C("GuideTitle", "Write-only actions and command sequences");
    public string GuideCommandsHelp => C("GuideHelp", "Commands stores fixed device targets and ordered writes. Each step can read back its value, check a separate status address/bit, or accept only the write response. ACK-only does not confirm that the device finished its action. Inspect the preview and run explicitly; writes are never retried automatically.");
    public string CommandEditorLabel => C("Editor", "Edit commands…");
    public string CommandSelectorLabel => C("Selector", "Saved command");
    public string ExecuteCommandLabel => C("Run", "Run selected command");
    public string CommandHelp => C("RunHelp", "Inspect the target and wire preview before running. Each step writes once. Only status reads repeat. Stop also stops polling. Applying, loading or reconnecting never runs commands.");
    public string CommandStatus { get => _commandStatus; private set => SetProperty(ref _commandStatus, value); }
    public string CommandResults { get => _commandResults; private set => SetProperty(ref _commandResults, value); }
    public string CommandEmptyHelp => C("Empty", "Define a command for a write-only action, a separate status check or an ordered sequence.");
    public Visibility CommandEmptyVisibility => CommandChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<ModbusCommandChoice> CommandChoices { get; } = [];
    public AsyncRelayCommand ExecuteCommand { get; }
    public long CommandSettingsRevision => _commandSettingsRevision;
    public bool IsCommandSelectionEnabled => !_commandAdmitted;
    private string ActiveCommandTargetSummary
    {
        get
        {
            if (_acceptCommandSnapshots && _service is IModbusRtuCommandService commands)
            {
                var snapshot = commands.GetCommandSnapshot();
                if (snapshot.CommandId == SelectedCommand?.Id && snapshot.CurrentStep > 0 && snapshot.CurrentStep <= snapshot.Steps.Count)
                {
                    var step = snapshot.Steps[snapshot.CurrentStep - 1];
                    return $"{snapshot.Name} · {step.StepNumber}/{snapshot.Steps.Count} · ID {step.UnitId} · FC {step.WriteFunction:00} · {step.WriteAddress}";
                }
            }
            return SelectedCommand?.DisplayName ?? "";
        }
    }

    public ModbusCommandChoice? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (_commandAdmitted || (value is not null && !CommandChoices.Contains(value)) || !SetProperty(ref _selectedCommand, value)) return;
            if (_commandSettings is not null) _commandSettings.SelectedCommandId = value?.Id;
            ClearCommandResults();
            OnPropertyChanged(nameof(CommandPreview));
            ExecuteCommand.NotifyCanExecuteChanged();
        }
    }

    public string CommandPreview
    {
        get
        {
            if (SelectedCommand?.Definition is not { } command) return CommandEmptyHelp;
            try
            {
                command.Validate();
                return string.Join("\n", command.Steps.Select((step, index) =>
                {
                    var check = step.Verification.Mode switch
                    {
                        ModbusCommandVerificationMode.ReadBack => C("CheckReadBack", "Read back the written value"),
                        ModbusCommandVerificationMode.AcknowledgementOnly => C("CheckAck", "ACK only · operation completion is not checked"),
                        _ => $"{C("CheckStatus", "Status")} FC {step.Verification.Function:00} · {step.Verification.Address} · (& 0x{step.Verification.Mask:X4}) = 0x{step.Verification.ExpectedValue:X4}"
                    };
                    var bytes = step.BuildPacket();
                    var packet = string.Join(" ", bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
                    return $"{index + 1}. {step.Name} · ID {step.UnitId} · FC {bytes[1]:00} · {step.Item.Address} · {step.Value} {step.Item.Unit}\n{check}\nHEX: {packet}";
                }));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ex.Message; }
        }
    }

    public ModbusCommandSettings CreateCommandEditorSettings() => _commandSettings?.Clone() ?? new();
    public ModbusCommandSettings? CreatePersistableCommandSettingsSnapshot() => _commandSettings?.Clone();

    public void ApplyCommandSettings(ModbusCommandSettings settings, long? expectedRevision = null)
    {
        if (!IsSettingsEnabled) throw new InvalidOperationException(T("StopBeforeSettings", "Stop Modbus before loading settings."));
        if (expectedRevision is { } revision && revision != CommandSettingsRevision)
            throw new InvalidOperationException(C("Changed", "Command settings changed while editing. Reopen the editor."));
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        ApplySavedCommandSettings(settings);
    }

    public void ApplySavedCommandSettings(ModbusCommandSettings? settings)
    {
        if (IsBusy) throw new InvalidOperationException(T("StopBeforeSettings", "Stop Modbus before loading settings."));
        _commandSettings = settings?.Clone();
        _commandSettingsRevision++;
        CommandChoices.Clear();
        if (_commandSettings?.Commands is { } commands)
            foreach (var command in commands.Take(ModbusCommandSettings.MaximumCommands + 1)) CommandChoices.Add(new(command));
        _selectedCommand = CommandChoices.FirstOrDefault(row => row.Id is not null && row.Id == _commandSettings?.SelectedCommandId);
        ClearCommandResults();
        foreach (var name in new[] { nameof(SelectedCommand), nameof(CommandPreview), nameof(CommandEmptyVisibility) }) OnPropertyChanged(name);
        try { _commandSettings?.Validate(); }
        catch (ArgumentException ex) { CommandStatus = ex.Message; }
        NotifyCommandAvailability();
    }

    private void ClearCommandResults()
    {
        _acceptCommandSnapshots = false;
        _lastCommandRevision = long.MinValue;
        CommandStatus = "";
        CommandResults = "";
    }

    private bool CanExecuteSelectedCommand() => _service is IModbusRtuCommandService && SelectedCommand?.Definition is not null &&
        !_commandAdmitted && !_writeAdmitted && !_stopRequested && (_service.GetSnapshot().IsPolling || CanRun());

    public async Task ExecuteSelectedCommandAsync()
    {
        if (!CanExecuteSelectedCommand()) { Error = C("Unavailable", "Select a command and connect an available device before running."); return; }
        ModbusCommandDefinition command;
        try
        {
            // Validate the complete workspace, including duplicate identities and malformed imports.
            _commandSettings!.Validate();
            command = SelectedCommand!.Definition!.Clone();
            ValidateCommandMonitorPermissions(command);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error = ex.Message; return; }
        ClearCommandResults();
        _commandAdmitted = true;
        Error = "";
        PublishAvailability();
        try
        {
            var executing = ((IModbusRtuCommandService)_service).ExecuteCommandAsync(command, CancellationToken.None);
            _acceptCommandSnapshots = true;
            RefreshCommandState();
            await executing;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; RuntimeDiagnostics.RecordError("Modbus.Command", ex); }
        finally { _commandAdmitted = false; Refresh(); PublishAvailability(); }
    }

    private void ValidateCommandMonitorPermissions(ModbusCommandDefinition command)
    {
        IEnumerable<ModbusMonitorSettings> monitors;
        if (_pollingSettings is not null)
        {
            CaptureSelectedGroupDraft(strict: true);
            _pollingSettings.Validate();
            monitors = _pollingSettings.Groups.Select(group => group.Settings);
        }
        else monitors = [CreateSettingsSnapshot()];
        foreach (var step in command.Steps)
        foreach (var monitor in monitors)
        {
            if (monitor.UnitId != step.UnitId || monitor.Function != step.Function) continue;
            var end = (long)step.Item.Address + step.Item.RegisterCount;
            foreach (var item in monitor.Items)
            {
                var itemEnd = (long)item.Address + item.RegisterCount;
                if (end <= item.Address || step.Item.Address >= itemEnd) continue;
                if (item.Access != SerialMonitor.Core.Modbus.ModbusItemAccess.ReadWrite)
                    throw new InvalidOperationException(C("ReadOnlyOverlap", "A command overlaps a read-only monitored item."));
                if (step.Item.Address > item.Address || end < itemEnd)
                    throw new ArgumentException(C("PartialOverlap", "A command must write the complete monitored multi-register item."));
            }
        }
    }

    private void RefreshCommandState()
    {
        if (!_acceptCommandSnapshots || _service is not IModbusRtuCommandService commands) return;
        var snapshot = commands.GetCommandSnapshot();
        if (snapshot.CommandId != SelectedCommand?.Id || snapshot.Revision == _lastCommandRevision) return;
        _lastCommandRevision = snapshot.Revision;
        CommandStatus = CommandStateText(snapshot.State);
        CommandResults = string.Join("\n", snapshot.Steps.Select(step =>
        {
            var observed = step.ObservedValues is { Length: > 0 } values ? " · " + string.Join(" ", values.Select(value => $"0x{value:X4}")) : "";
            var checkAddress = step.VerificationFunction is { } function ? $" · {C("CheckStatus", "Status")} FC {function:00} · {step.VerificationAddress}" : "";
            var acknowledgement = step.WriteAcknowledged ? " · " + C("AckReceived", "write ACK received") : "";
            return $"{step.StepNumber}. {step.Name} · ID {step.UnitId} · FC {step.WriteFunction:00} · {step.WriteAddress} · {StepStateText(step.State)}{acknowledgement}{checkAddress}{observed}" +
                (step.Error is { Length: > 0 } error ? "\n" + error : "");
        }));
        if (!string.IsNullOrWhiteSpace(snapshot.Error)) CommandStatus += " · " + snapshot.Error;
    }

    private static string CommandStateText(ModbusCommandState state) => state switch
    {
        ModbusCommandState.Queued => C("Queued", "Command queued"),
        ModbusCommandState.Running => C("Running", "Command running"),
        ModbusCommandState.Acknowledged => C("Acknowledged", "Write responses received · operation completion is not checked for ACK-only steps"),
        ModbusCommandState.Verified => C("Verified", "All configured result checks passed"),
        ModbusCommandState.NotVerified => C("NotVerified", "Result condition was not met · stopped before the next step"),
        ModbusCommandState.Canceled => C("Canceled", "Command stopped · acknowledged writes may have taken effect"),
        ModbusCommandState.Failed => C("Failed", "Command failed · inspect before retrying"),
        _ => ""
    };

    private static string StepStateText(ModbusCommandStepState state) => state switch
    {
        ModbusCommandStepState.Pending => C("Pending", "Waiting"),
        ModbusCommandStepState.Writing => C("Writing", "Writing"),
        ModbusCommandStepState.Acknowledged => C("StepAcknowledged", "ACK only · operation completion is not checked"),
        ModbusCommandStepState.Checking => C("Checking", "Checking result"),
        ModbusCommandStepState.Verified => C("StepVerified", "Result verified"),
        ModbusCommandStepState.NotVerified => C("StepNotVerified", "Result not verified"),
        ModbusCommandStepState.Canceled => C("StepCanceled", "Not continued"),
        _ => C("StepFailed", "Failed")
    };

    private void NotifyCommandAvailability()
    {
        OnPropertyChanged(nameof(IsCommandSelectionEnabled));
        OnPropertyChanged(nameof(RequestSummary));
        ExecuteCommand?.NotifyCanExecuteChanged();
    }
}

public sealed class ModbusCommandChoice(ModbusCommandDefinition? definition)
{
    internal ModbusCommandDefinition? Definition { get; } = definition;
    public string? Id => Definition?.Id;
    public string DisplayName => Definition is null ? "—" : $"{Definition.Name} · {Definition.Steps?.Count ?? 0}";
}
