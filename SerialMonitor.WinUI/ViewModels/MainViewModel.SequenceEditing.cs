using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private SequenceExecutionControl? _sequenceExecutionControl;

    public AsyncRelayCommand ToggleCommandSequenceCommand { get; private set; } = null!;

    private bool CanControlGuiSequence => IsSequenceRunning && _sequenceExecutionControl is not null &&
        _sequenceCancellation?.IsCancellationRequested == false;

    public string SequenceRunButtonGlyph => IsSequenceRunning && _sequenceExecutionControl?.IsPauseRequested != true
        ? "\uE769" : "\uE768";

    public string SequenceRunButtonToolTip => !IsSequenceRunning
        ? UiText.Get("SequenceStartAction", "Run sequence")
        : _sequenceExecutionControl?.IsPauseRequested == true
            ? _sequenceExecutionControl.IsPaused
                ? UiText.Get("SequenceResumeAction", "Resume sequence")
                : UiText.Get("SequencePausePendingAction", "Cancel pause and continue sequence")
            : UiText.Get("SequencePauseAction", "Pause after the current send and delay finish");

    public int SelectedCommandSequenceEnabledStepCount => SelectedCommandSequence?.Steps.Count(step => step.IsEnabled) ?? 0;

    private void InitializeSequenceDebugCommands()
    {
        ToggleCommandSequenceCommand = new AsyncRelayCommand(() =>
        {
            if (!IsSequenceRunning)
            {
                // The run command owns the long-running operation. Keeping this
                // toggle command short lets the same button pause an active run.
                RunCommandSequenceCommand.Execute(null);
            }
            else if (CanControlGuiSequence)
            {
                if (_sequenceExecutionControl!.IsPauseRequested)
                {
                    _sequenceExecutionControl.Resume();
                    RecordSequenceStatus("Sequence resumed.");
                }
                else
                {
                    _sequenceExecutionControl.Pause();
                    RecordSequenceStatus("Sequence pause requested; the current send and delay will finish first.");
                }
            }
            return Task.CompletedTask;
        }, () => IsSequenceRunning ? CanControlGuiSequence : RunCommandSequenceCommand.CanExecute(null));
        // The run command finishes after IsSequenceRunning is cleared. Propagate
        // its final CanExecute change so the start button becomes available again.
        RunCommandSequenceCommand.CanExecuteChanged += (_, _) => ToggleCommandSequenceCommand.NotifyCanExecuteChanged();
    }

    private void OnSequenceExecutionControlChanged()
    {
        RunOnUiThread(() =>
        {
            if (!CanControlGuiSequence) return;
            _sequenceTxActivity.SetPhase(_sequenceExecutionControl!.IsPaused ? "Paused" :
                _sequenceExecutionControl.IsPauseRequested ? "Pause requested" : "Running");
            RefreshSequenceTxActivity();
            NotifySequenceDebugCommandStates();
        });
    }

    private void NotifySequenceDebugCommandStates()
    {
        OnPropertyChanged(nameof(SequenceRunButtonGlyph));
        OnPropertyChanged(nameof(SequenceRunButtonToolTip));
        OnPropertyChanged(nameof(SelectedCommandSequenceEnabledStepCount));
        OnPropertyChanged(nameof(SequenceRuntimeStateText));
        OnPropertyChanged(nameof(SequenceStatusText));
        ToggleCommandSequenceCommand?.NotifyCanExecuteChanged();
    }

    public bool SetCommandSequenceStepEnabled(CommandSequenceStep step, bool enabled)
    {
        if (!EnsureCanEditCommandSequences("Change sequence step") ||
            SelectedCommandSequence?.Steps.Contains(step) != true)
            return false;
        // Two-way binding may have already assigned this value before Click.
        step.IsEnabled = enabled;
        ApplyCommandSequenceChanges($"{(enabled ? "Enabled" : "Disabled")} sequence step: {step.DisplayName}");
        return true;
    }

    public bool DuplicateSelectedCommandSequenceStep()
    {
        if (!EnsureCanEditCommandSequences("Duplicate sequence step") ||
            SelectedCommandSequence is not { } sequence || SelectedCommandSequenceStep is not { } selected)
            return false;
        var index = sequence.Steps.IndexOf(selected);
        if (index < 0) return false;
        var copy = CloneCommandSequenceStep(selected);
        sequence.Steps.Insert(index + 1, copy);
        SelectedCommandSequenceStep = copy;
        ApplyCommandSequenceChanges($"Duplicated sequence step: {copy.DisplayName}");
        return true;
    }

    public bool DuplicateSelectedCommandSequence()
    {
        if (!EnsureCanEditCommandSequences("Duplicate sequence") || SelectedCommandSequence is not { } selected)
            return false;
        var copy = CloneCommandSequence(selected);
        var stem = selected.Name + " (copy)";
        copy.Name = stem;
        var suffix = 2;
        while (CommandSequences.Any(existing => string.Equals(existing.Name, copy.Name, StringComparison.OrdinalIgnoreCase)))
            copy.Name = $"{stem} {suffix++}";
        return AddCommandSequence(copy);
    }
}
