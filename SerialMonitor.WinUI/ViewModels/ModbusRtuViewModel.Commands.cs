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
    private bool _commandAdmitted, _acceptCommandSnapshots, _commandOwnsHeader, _replacingCommandChoices;
    private long _commandSettingsRevision, _lastCommandRevision = long.MinValue;
    private string _commandStatus = "", _commandResults = "", _commandTargetSummary = "";
    private string _recentCommandSummary = "", _recentCommandResults = "";
    private string _recentCommandCaption = "", _recentCommandAlert = "", _activeCommandName = "";
    private ModbusCommandSnapshot? _presentedCommandSnapshot;

    private static string C(string key, string fallback) => UiText.Get("ModbusCommand" + key, fallback);
    public string CommandsLabel => C("Actions", "Commands…");
    public string GuideCommandsTitle => C("GuideTitle", "Write-only actions and command sequences");
    public string GuideCommandsHelp => C("GuideHelp", "Commands stores fixed device targets and ordered writes. Each step can read back its value, check a separate status address/bit, or accept only the write response. ACK-only does not confirm that the device finished its action. Inspect the preview and run explicitly; writes are never retried automatically.");
    public string CommandEditorLabel => C("Editor", "Edit commands…");
    public string CommandSelectorLabel => C("Selector", "Saved command");
    public string ExecuteCommandLabel => C("Run", "Run selected command");
    public string CommandHelp => C("RunHelp", "Commands use fixed unit IDs and addresses, independently of the selected monitoring group. Check the target, value and result check before running. Writes are never retried. Stop also stops polling.");
    public string StopAllLabel => C("StopAll", "Stop command and polling");
    public string WirePreviewLabel => C("WirePreview", "RTU packet details (HEX)");
    public string CommandResultDetailsLabel => C("ResultDetails", "Step details");
    public string RecentCommandCaption { get => _recentCommandCaption; private set => SetProperty(ref _recentCommandCaption, value); }
    public string RecentCommandAlert
    {
        get => _recentCommandAlert;
        private set { if (SetProperty(ref _recentCommandAlert, value)) OnPropertyChanged(nameof(RecentCommandAlertVisibility)); }
    }
    public Visibility RecentCommandAlertVisibility => RecentCommandAlert.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string RecentCommandSummary { get => _recentCommandSummary; private set { if (SetProperty(ref _recentCommandSummary, value)) { OnPropertyChanged(nameof(RecentCommandVisibility)); OnPropertyChanged(nameof(CommandsMenuLabel)); } } }
    public string RecentCommandResults { get => _recentCommandResults; private set => SetProperty(ref _recentCommandResults, value); }
    public Visibility RecentCommandVisibility => RecentCommandSummary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string CommandStatus
    {
        get => _commandStatus;
        private set
        {
            if (SetProperty(ref _commandStatus, value) && _commandOwnsHeader) OnPropertyChanged(nameof(Status));
        }
    }
    public string CommandResults { get => _commandResults; private set => SetProperty(ref _commandResults, value); }
    public string CommandEmptyHelp => C("Empty", "Define a command for a write-only action, a separate status check or an ordered sequence.");
    public Visibility CommandEmptyVisibility => CommandChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<ModbusCommandChoice> CommandChoices { get; } = [];
    public AsyncRelayCommand ExecuteCommand { get; }
    public long CommandSettingsRevision => _commandSettingsRevision;
    public bool IsCommandSelectionEnabled => !_commandAdmitted;
    private void SetCommandTargetSummary(string name, int step, int count, int unitId, byte function, int address)
    {
        SetProperty(ref _commandTargetSummary, $"{name} · {step}/{count} · ID {unitId} · FC {function:00} · {address}", nameof(RequestSummary));
    }

    private void ReleaseCommandHeader()
    {
        if (!_commandOwnsHeader || _commandAdmitted) return;
        _commandOwnsHeader = false;
        NotifyCommandHeader();
    }

    private void NotifyCommandHeader()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(RequestSummary));
    }

    public ModbusCommandChoice? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (_replacingCommandChoices || _commandAdmitted || (value is not null && !CommandChoices.Contains(value)) || !SetProperty(ref _selectedCommand, value)) return;
            if (_commandSettings is not null) _commandSettings.SelectedCommandId = value?.Id;
            ClearCommandResults();
            OnPropertyChanged(nameof(CommandPreview));
            OnPropertyChanged(nameof(CommandTargetPreview));
            OnPropertyChanged(nameof(CommandWirePreview));
            ExecuteCommand.NotifyCanExecuteChanged();
        }
    }

    public string CommandPreview => BuildCommandPreview(includeWire: true);
    public string CommandTargetPreview => BuildCommandPreview(includeWire: false);
    public string CommandWirePreview
    {
        get
        {
            if (SelectedCommand?.Definition is not { } command) return "";
            try
            {
                command.Validate();
                return string.Join("\n", command.Steps.Select((step, index) =>
                    $"{index + 1}. " + string.Join(" ", step.BuildPacket().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)))));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ex.Message; }
        }
    }
    private string BuildCommandPreview(bool includeWire)
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
                var target = $"{index + 1}. {step.Name} · ID {step.UnitId} · FC {bytes[1]:00} · {step.Item.Address} ← {step.Value} {step.Item.Unit}".TrimEnd();
                return includeWire ? target + "\n" + check + "\nHEX: " + packet : target + " · " + check;
            }));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ex.Message; }
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
        _replacingCommandChoices = true;
        try
        {
            CommandChoices.Clear();
            if (_commandSettings?.Commands is { } commands)
                foreach (var command in commands.Take(ModbusCommandSettings.MaximumCommands + 1)) CommandChoices.Add(new(command));
            _selectedCommand = CommandChoices.FirstOrDefault(row => row.Id is not null && row.Id == _commandSettings?.SelectedCommandId);
        }
        finally { _replacingCommandChoices = false; }
        ClearCommandResults();
        RecentCommandSummary = "";
        RecentCommandResults = "";
        RecentCommandCaption = RecentCommandAlert = "";
        foreach (var name in new[] { nameof(SelectedCommand), nameof(CommandPreview), nameof(CommandTargetPreview), nameof(CommandWirePreview), nameof(CommandEmptyVisibility) }) OnPropertyChanged(name);
        try { _commandSettings?.Validate(); }
        catch (ArgumentException ex) { CommandStatus = ex.Message; }
        NotifyCommandAvailability();
    }

    private void ClearCommandResults()
    {
        ReleaseCommandHeader();
        _acceptCommandSnapshots = false;
        _lastCommandRevision = long.MinValue;
        CommandStatus = "";
        CommandResults = "";
        _commandTargetSummary = "";
        _activeCommandName = "";
        _presentedCommandSnapshot = null;
    }

    private bool CanExecuteSelectedCommand() => _service is IModbusRtuCommandService && SelectedCommand?.Definition is not null &&
        !_commandAdmitted && !_writeAdmitted && !_stopRequested && (_service.GetSnapshot().IsPolling || CanRun());

    public async Task ExecuteSelectedCommandAsync()
    {
        if (!CanExecuteSelectedCommand()) { Error = C("Unavailable", "Select a command and connect an available device before running."); return; }
        ModbusCommandDefinition command;
        byte firstWriteFunction;
        try
        {
            // Validate the complete workspace, including duplicate identities and malformed imports.
            _commandSettings!.Validate();
            command = SelectedCommand!.Definition!.Clone();
            ValidateCommandMonitorPermissions(command);
            firstWriteFunction = command.Steps[0].BuildPacket()[1];
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error = ex.Message; return; }
        ClearCommandResults();
        _activeCommandName = command.Name;
        _commandAdmitted = true;
        _commandOwnsHeader = true;
        var first = command.Steps[0];
        SetCommandTargetSummary(command.Name, 1, command.Steps.Count, first.UnitId, firstWriteFunction, first.Item.Address);
        Error = "";
        PublishAvailability();
        try
        {
            var executing = ((IModbusRtuCommandService)_service).ExecuteCommandAsync(command, CancellationToken.None);
            _acceptCommandSnapshots = true;
            RefreshCommandState();
            await executing;
        }
        catch (OperationCanceledException)
        {
            if (!_acceptCommandSnapshots) CommandStatus = CommandStateText(ModbusCommandState.Canceled);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            if (!_acceptCommandSnapshots) CommandStatus = CommandStateText(ModbusCommandState.Failed) + " · " + ex.Message;
            RuntimeDiagnostics.RecordError("Modbus.Command", ex);
        }
        finally
        {
            _commandAdmitted = false;
            // A resumed poll owns the current header again. Standalone terminal
            // results retain their captured command target until the next action.
            if (_service.GetSnapshot().IsPolling) ReleaseCommandHeader();
            _lastRevision = long.MinValue;
            Refresh();
            PublishRecentCommand();
            NotifyCommandHeader();
            PublishAvailability();
        }
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
        _presentedCommandSnapshot = snapshot;
        if (snapshot.CurrentStep > 0 && snapshot.CurrentStep <= snapshot.Steps.Count)
        {
            var current = snapshot.Steps[snapshot.CurrentStep - 1];
            SetCommandTargetSummary(snapshot.Name ?? "", current.StepNumber, snapshot.Steps.Count, current.UnitId,
                current.WriteFunction, current.WriteAddress);
        }
        CommandStatus = CommandStateText(snapshot.State);
        CommandResults = string.Join("\n", snapshot.Steps.Select(step =>
        {
            var observed = step.ObservedValues is { Length: > 0 } values ? " = " + string.Join(" ", values.Select(value => $"0x{value:X4}")) : "";
            var checkAddress = step.VerificationFunction is { } function ? $"\n{C("Verification", "Result check")} FC {function:00} · {step.VerificationAddress}{observed}" : "";
            var acknowledgement = step.WriteAcknowledged && step.State != ModbusCommandStepState.Verified
                ? " · " + C("AckReceived", "write ACK received") : "";
            return $"{step.StepNumber}. {step.Name} · {StepStateText(step.State)}{acknowledgement}\nID {step.UnitId} · FC {step.WriteFunction:00} · {step.WriteAddress}{checkAddress}" +
                (step.Error is { Length: > 0 } error ? "\n" + error : "");
        }));
        if (!string.IsNullOrWhiteSpace(snapshot.Error)) CommandStatus += " · " + snapshot.Error;
        PublishRecentCommand();
    }

    private void PublishRecentCommand()
    {
        if (_commandTargetSummary.Length == 0 || CommandStatus.Length == 0) return;
        RecentCommandSummary = C("Recent", "Recent command") + ": " + _commandTargetSummary + " · " + CommandStatus;
        RecentCommandResults = CommandResults;
        var snapshot = _presentedCommandSnapshot;
        RecentCommandCaption = C("Recent", "Recent command") + ": " + (snapshot is null ? _activeCommandName + " · " + CommandStatus
            : $"{snapshot.Name ?? _activeCommandName} · {CompactCommandState(snapshot.State)} · {snapshot.CurrentStep}/{snapshot.Steps.Count}");
        RecentCommandAlert = snapshot?.State switch
        {
            ModbusCommandState.Failed or ModbusCommandState.NotVerified => !string.IsNullOrWhiteSpace(snapshot.Error) ? snapshot.Error
                : snapshot.Steps.FirstOrDefault(step => !string.IsNullOrWhiteSpace(step.Error))?.Error ?? "",
            ModbusCommandState.Canceled => C("StoppedWarning", "Sent writes may already have taken effect."),
            _ => ""
        };
    }

    private static string CompactCommandState(ModbusCommandState state) => state switch
    {
        ModbusCommandState.Queued => C("ShortQueued", "Queued"),
        ModbusCommandState.Running => C("ShortRunning", "Running"),
        ModbusCommandState.Verified => C("ShortVerified", "Verified"),
        ModbusCommandState.Acknowledged => C("ShortAcknowledged", "ACK received · completion unchecked"),
        ModbusCommandState.NotVerified => C("ShortNotVerified", "Condition not met"),
        ModbusCommandState.Canceled => C("ShortCanceled", "Stopped"),
        ModbusCommandState.Failed => C("ShortFailed", "Failed"),
        _ => ""
    };

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
