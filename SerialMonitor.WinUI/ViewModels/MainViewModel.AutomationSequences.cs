using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private string? _automationSequenceRunId;
    private const int MaxAutomationSequences = 1000;

    private CommandSequence FindAutomationSequence(JsonElement args)
    {
        var name = args.String("name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new AutomationException("invalid_argument", "name must contain 1..128 characters.");
        return CommandSequences.FirstOrDefault(sequence => string.Equals(sequence.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new AutomationException("sequence_not_found", "Sequence not found: " + name);
    }

    private async Task<object> InvokeSequenceAutomationOnUiAsync(string operation, JsonElement args, CancellationToken token)
    {
        switch (operation)
        {
            case "sequence_list":
            {
                var offset = args.Integer("offset", 0);
                var limit = args.Integer("limit", 100);
                if (offset < 0 || offset > CommandSequences.Count || limit is < 1 or > 100)
                    throw new AutomationException("invalid_argument", "Use offset 0..totalCount and limit 1..100.");
                var page = CommandSequences.Skip(offset).Take(limit).ToArray();
                if (page.Any(sequence => sequence.Name.Length > 128))
                    throw new AutomationException("definition_too_large", "A legacy sequence name exceeds 128 characters. Rename it in the GUI first.");
                return new
                {
                    sequences = page.Select(sequence => new { sequence.Name, sequence.RepeatCount, stepCount = sequence.Steps.Count }).ToArray(),
                    offset, nextOffset = offset + page.Length, hasMore = offset + page.Length < CommandSequences.Count,
                    totalCount = CommandSequences.Count
                };
            }
            case "sequence_get":
            {
                var sequence = FindAutomationSequence(args);
                // Legacy GUI sequences can be much larger than the RPC contract.
                // Reject before allocating a full JSON copy or truncating commands.
                if (sequence.Steps.Count(step => step.IsEnabled) > 128 || sequence.Steps.Where(step => step.IsEnabled).Sum(step => (long)step.CommandText.Length + (step.Name?.Length ?? 0) + (step.Expect?.Value.Length ?? 0)) > 48 * 1024)
                    throw new AutomationException("definition_too_large", "This GUI sequence exceeds automation limits. Split it into smaller sequences.");
                var snapshot = AutomationSequenceMapper.Snapshot(sequence, SelectedTxSendMode, SelectedTxLineEnding);
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(snapshot, AutomationJson.Options)) > 48 * 1024)
                    throw new AutomationException("definition_too_large", "Sequence exceeds 48 KiB. Split it into smaller sequences.");
                return snapshot;
            }
            case "sequence_save":
            {
                EnsureAutomationSequenceReservation();
                if (!args.TryGetProperty("sequence", out var input))
                    throw new AutomationException("invalid_argument", "sequence is required.");
                var definition = AutomationSequenceValidation.Parse(input);
                var replacement = AutomationSequenceMapper.Create(definition);
                var existing = CommandSequences.FirstOrDefault(sequence => string.Equals(sequence.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
                var overwrite = args.TryGetProperty("overwrite", out var flag) && flag.GetBoolean();
                if (existing is not null && !overwrite)
                    throw new AutomationException("sequence_exists", "Sequence already exists. Review it and set overwrite=true to replace it.");
                if (existing is null && CommandSequences.Count >= MaxAutomationSequences)
                    throw new AutomationException("sequence_limit", "At most 1000 sequences can be registered through automation.");
                var profile = CreateProfileFromCurrentState();
                var index = existing is null ? -1 : CommandSequences.IndexOf(existing);
                if (index < 0) profile.CommandSequences.Add(replacement);
                else profile.CommandSequences[index] = replacement;
                if (existing is not null) UpdateAutomationProfileReferences(profile, existing.Name, replacement.Name);
                await PersistAutomationSequenceProfileAsync(profile, token);
                // Persistence is the commit point. Do not report cancellation or
                // leave the GUI stale after the atomic profile replacement.
                if (index < 0) CommandSequences.Add(replacement);
                else CommandSequences[index] = replacement;
                if (existing is not null) UpdateRuleSequenceReferences(existing.Name, replacement.Name);
                SelectedCommandSequence = replacement;
                ApplyCommandSequenceChanges("AI/CLI saved sequence: " + replacement.Name);
                return new { name = replacement.Name, persisted = true, replaced = existing is not null, stepCount = replacement.Steps.Count };
            }
            case "sequence_delete":
            {
                EnsureAutomationSequenceReservation();
                var existing = FindAutomationSequence(args);
                var index = CommandSequences.IndexOf(existing);
                var profile = CreateProfileFromCurrentState();
                profile.CommandSequences.RemoveAt(index);
                UpdateAutomationProfileReferences(profile, existing.Name, null);
                await PersistAutomationSequenceProfileAsync(profile, token);
                CommandSequences.RemoveAt(index);
                UpdateRuleSequenceReferences(existing.Name, null);
                if (ReferenceEquals(SelectedCommandSequence, existing))
                    SelectedCommandSequence = CommandSequences.Count == 0 ? null : CommandSequences[Math.Min(index, CommandSequences.Count - 1)];
                ApplyCommandSequenceChanges("AI/CLI deleted sequence: " + existing.Name);
                return new { name = existing.Name, deleted = true, persisted = true };
            }
            case "sequence_progress":
                ApplyAutomationSequenceProgress(args);
                return new { recorded = true };
            default: throw new AutomationException("unknown_operation", "Unsupported sequence operation.");
        }
    }

    private void EnsureAutomationSequenceReservation()
    {
        if (!IsAutomationBusy) throw new AutomationException("busy", "No control reservation.");
    }

    private static void UpdateAutomationProfileReferences(AppProfile profile, string previous, string? replacement)
    {
        foreach (var rule in profile.LogRules.Where(rule => string.Equals(rule.TriggerSequenceName, previous, StringComparison.OrdinalIgnoreCase)))
            rule.TriggerSequenceName = replacement;
        foreach (var rule in profile.EventRules.Where(rule => string.Equals(rule.TriggerSequenceName, previous, StringComparison.OrdinalIgnoreCase)))
            rule.TriggerSequenceName = replacement;
    }

    private async Task PersistAutomationSequenceProfileAsync(AppProfile profile, CancellationToken token)
    {
        try
        {
            await Task.Run(() => _profileService.SaveAsync(CurrentProfilePath, profile, token), token);
            RefreshProfileProperties();
        }
        catch (OperationCanceledException) { throw; }
        catch (ProfileConflictException ex)
        {
            RefreshProfileProperties();
            RecordSequenceError("AI/CLI sequence save conflict: " + ex.Message);
            throw new AutomationException("profile_conflict", ex.Message);
        }
        catch (Exception ex)
        {
            RefreshProfileProperties();
            RecordSequenceError("AI/CLI sequence save failed: " + ex.Message);
            throw new AutomationException("persistence_failed", "Profile save failed; GUI sequences were not changed. " + ex.Message);
        }
    }

    private void ApplyAutomationSequenceProgress(JsonElement args)
    {
        var runId = args.String("runId")!;
        var name = args.String("name") ?? "AI/CLI";
        var state = args.String("state") ?? "running";
        var completed = args.Integer("completedSteps", 0);
        var total = args.Integer("totalSteps", 0);
        var step = args.Integer("stepIndex", -1);
        if (_automationSequenceRunId != runId)
        {
            _automationSequenceRunId = runId;
            IsSequenceRunning = true;
            RunningSequenceName = name;
            _runningSequenceTotalSteps = total;
            _sequenceTxActivity.Begin(name, total);
            _lastSequenceError = string.Empty;
            Interlocked.Increment(ref _sequenceRunCount);
            OnPropertyChanged(nameof(SequenceRunCount));
            OnPropertyChanged(nameof(LastSequenceError));
        }
        CompletedSequenceSteps = completed;
        CurrentSequenceStepText = step < 0 ? "AI/CLI starting" : $"AI/CLI step {step + 1}/{total}";
        _sequenceTxActivity.UpdateAutomationProgress(args.Integer("sentCount", completed), args.Integer("failedAttempts", 0),
            $"AI/CLI {state} · {completed}/{total}");
        RefreshSequenceTxActivity();
        if (state != "running")
        {
            _automationLastResult = $"AI/CLI sequence {state}: {name} ({completed}/{total})";
            if (args.String("error") is { } error) RecordSequenceError(_automationLastResult + ": " + error);
            else RecordSequenceStatus(_automationLastResult);
            IsSequenceRunning = false;
            RunningSequenceName = "(none)";
            CurrentSequenceStepText = "(none)";
            _runningSequenceTotalSteps = 0;
            _automationSequenceRunId = null;
        }
        else SetAutomationStatus($"AI/CLI sequence: {name} ({completed}/{total})");
        NotifyCommandStates();
        RefreshStatusBar();
    }
}
