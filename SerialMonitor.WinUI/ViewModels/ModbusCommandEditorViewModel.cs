using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

// Offline command authoring only. Drafts never acquire a port, execute a command
// or save a profile; the caller applies the final private configuration copy.
public sealed class ModbusCommandEditorViewModel : ViewModelBase
{
    private ModbusCommandEditorEntry? _selectedCommand;
    private ModbusCommandStepEditorEntry? _selectedStep;
    private string? _selectedCommandId;
    private string _commandId = Guid.NewGuid().ToString("N");
    private bool _loading, _navigating, _commandDirty, _stepDirty, _invalidMetadata;
    private string _commandName = "", _stepName = "", _unit = "1", _address = "0", _value = "", _scale = "1", _engineeringUnit = "", _valueNames = "", _timeout = "1000";
    private string _statusAddress = "0", _mask = "0xFFFF", _expected = "0", _verificationTimeout = "3000", _verificationInterval = "100", _error = "";
    private string _itemName = "";
    private int _areaIndex = 1, _formatIndex, _verificationIndex, _statusFunctionIndex = 2;
    private bool _swapBytes, _swapWords;
    private bool _isAdvancedExpanded;

    public ModbusCommandEditorViewModel(ModbusCommandSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        _selectedCommandId = copy.SelectedCommandId;
        _invalidMetadata = copy.HasInvalidMetadata;
        if (copy.Commands is null) Items.Add(new(null));
        else foreach (var command in copy.Commands) Items.Add(new(command?.Clone()));
        var selected = Items.FirstOrDefault(entry => entry.Definition is { } command && command.Id == copy.SelectedCommandId);
        if (selected is null && copy.SelectedCommandId is null) selected = Items.FirstOrDefault();
        if (selected is not null) SelectedCommand = selected;
        else ResetCommandDraft();
    }

    private static string T(string key, string fallback) => UiText.Get("ModbusCommand" + key, fallback);
    private static string M(string key, string fallback) => UiText.Get("ModbusMonitor" + key, fallback);
    public string Title => T("EditorTitle", "Command definitions");
    public string ApplyLabel => T("Apply", "Apply to monitor");
    public string CancelLabel => T("Cancel", "Cancel");
    public string Help => T("EditorHelp", "Valid edits are included when you select another command or step, choose New, or apply. Correct invalid fields before moving on. Applying never sends; use Save profile to keep definitions for later.");
    public string CommandsLabel => T("List", "Commands");
    public string StepsLabel => T("Steps", "Steps (execution order)");
    public string NewCommandLabel => T("New", "New command");
    public string DuplicateCommandLabel => T("Duplicate", "Duplicate");
    public string RemoveCommandLabel => T("Remove", "Remove");
    public string ClearCommandsLabel => T("Clear", "Clear all");
    public string SaveCommandLabel => T("Save", "Add / update command");
    public string NewStepLabel => T("NewStep", "New step");
    public string DuplicateStepLabel => T("DuplicateStep", "Duplicate step");
    public string RemoveStepLabel => T("RemoveStep", "Remove step");
    public string SaveStepLabel => T("SaveStep", "Add / update step");
    public string MoveUpLabel => T("MoveUp", "Move up");
    public string MoveDownLabel => T("MoveDown", "Move down");
    public string CommandNameLabel => T("Name", "Command name");
    public string StepNameLabel => T("StepName", "Step name");
    public string UnitLabel => M("Unit", "Unit ID (1–247)");
    public string AddressLabel => T("WriteAddress", "Write address (base 0)");
    public string AreaLabel => T("WriteArea", "Write area");
    public string FormatLabel => M("Format", "Display type");
    public string ScaleLabel => M("Scale", "Display multiplier");
    public string EngineeringUnitLabel => T("Unit", "Unit");
    public string SwapBytesLabel => M("SwapBytes", "Swap bytes");
    public string SwapWordsLabel => M("SwapWords", "Swap words (32-bit)");
    public string ValueLabel => T("Value", "Value or defined value name");
    public string TimeoutLabel => T("ResponseTimeout", "Response timeout (100–10000 ms)");
    public string VerificationLabel => T("Verification", "Result check");
    public string StatusFunctionLabel => T("StatusFunction", "Status read function");
    public string StatusAddressLabel => T("StatusAddress", "Status address (base 0)");
    public string MaskLabel => T("Mask", "Raw mask (0x HEX accepted)");
    public string ExpectedLabel => T("Expected", "Expected masked value");
    public string VerificationTimeoutLabel => T("VerificationTimeout", "Check timeout (100–60000 ms)");
    public string VerificationIntervalLabel => T("VerificationInterval", "Check interval (100–60000 ms)");
    public string ValueNamesLabel => T("ValueNames", "Value names (optional)");
    public string AdvancedLabel => T("Advanced", "Value type, scaling and communication options");
    public string WirePreviewLabel => T("WirePreview", "RTU packet details (HEX)");
    public string HelpLabel => M("Guide", "How to use");
    public string InterpretationSummary => FormatIndex is >= 0 and < 5
        ? $"{Formats[FormatIndex]} · ×{ScaleText}" + (EngineeringUnitText.Length == 0 ? "" : " · " + EngineeringUnitText)
            + (SwapBytes ? " · " + SwapBytesLabel : "") + (SwapWords ? " · " + SwapWordsLabel : "")
        : T("ChooseInterpretation", "Choose a value type in the advanced options.");
    public bool IsAdvancedExpanded { get => _isAdvancedExpanded; set => SetProperty(ref _isAdvancedExpanded, value); }
    public string ValueNamesHelp => T("ValueNamesHelp", "One decoded number = name per line, before the multiplier. Example: 0=Manual and 1=Automatic on separate lines.");
    public string DraftHelp => _stepDirty || _commandDirty
        ? T("UnsavedDraft", "Current edits are included automatically when moving to another command or step, choosing New, or applying. Invalid edits stay here until corrected.") : "";
    public string VerificationHelp => VerificationIndex switch
    {
        0 => T("ReadBackHelp", "After the write response, read the written address and compare the raw values. Use this for persistent settings, not self-clearing commands."),
        1 => T("StatusHelp", "After the write response, read one status register/bit from the same unit until (raw value & mask) equals the expected value. Only reads repeat; the write is not sent again."),
        2 => T("AcknowledgementHelp", "Only the write response is checked. Acknowledged does not mean the device action has completed. No readback is sent; this supports write-only or self-clearing commands."),
        _ => T("ChooseVerification", "Choose how to confirm this step.")
    };
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => Error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility StatusVisibility => VerificationIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsRegister => AreaIndex == 1;
    public bool CanEditFormat => IsRegister || FormatIndex != 0;
    public bool CanEditScale => IsRegister || ScaleText != "1";
    public bool CanSwapBytes => IsRegister || SwapBytes;
    public bool CanSwapWords => IsRegister && FormatIndex >= 2 || SwapWords;
    public IReadOnlyList<string> Areas => [T("Coils", "Coils"), T("HoldingRegisters", "Holding registers")];
    public IReadOnlyList<string> Formats { get; } = ["UInt16", "Int16", "UInt32", "Int32", "Float32"];
    public IReadOnlyList<string> VerificationModes => [T("ReadBack", "Read back written values"), T("Status", "Check another status address"), T("AcknowledgementOnly", "Write response only")];
    public IReadOnlyList<string> StatusFunctions { get; } = ["01 · Coils", "02 · Discrete inputs", "03 · Holding registers", "04 · Input registers"];
    public ObservableCollection<ModbusCommandEditorEntry> Items { get; } = [];
    public ObservableCollection<ModbusCommandStepEditorEntry> Steps { get; } = [];

    public ModbusCommandEditorEntry? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (_loading || _navigating || ReferenceEquals(_selectedCommand, value) || (value is not null && !Items.Contains(value))) return;
            _navigating = true;
            try
            {
                if (!CommitNavigationDraft()) { OnPropertyChanged(); return; }
                if (value is null) { ResetCommandDraft(); return; }
                SetSelectedCommand(value);
                if (value is not null)
                {
                    _selectedCommandId = value.Definition?.Id;
                    LoadCommand(value.Definition);
                }
            }
            finally { _navigating = false; }
        }
    }
    public ModbusCommandStepEditorEntry? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (_loading || _navigating || ReferenceEquals(_selectedStep, value) || (value is not null && !Steps.Contains(value))) return;
            var index = value is null ? -1 : Steps.IndexOf(value);
            _navigating = true;
            try
            {
                if (!CommitStepNavigationDraft()) { OnPropertyChanged(); return; }
                if (index < 0) { ResetStepDraft(); return; }
                // Saving replaces the current row. Resolve the requested
                // position again instead of retaining a detached row.
                var selected = Steps[index];
                SetSelectedStep(selected);
                if (selected is not null) LoadStep(selected.Definition);
            }
            finally { _navigating = false; }
        }
    }
    private void SetSelectedCommand(ModbusCommandEditorEntry? value) => SetProperty(ref _selectedCommand, value, nameof(SelectedCommand));
    private void SetSelectedStep(ModbusCommandStepEditorEntry? value) => SetProperty(ref _selectedStep, value, nameof(SelectedStep));
    private bool CommitNavigationDraft() => !(_commandDirty || _stepDirty) || SaveCommand();
    // Other imported steps may still need repair. Moving inside this command
    // validates only the current step and keeps the entire private draft.
    private bool CommitStepNavigationDraft() => !_stepDirty || SaveStep();
    public string CommandNameText
    {
        get => _commandName;
        set
        {
            if (!SetProperty(ref _commandName, value ?? "") || _loading) return;
            _commandDirty = true;
            Error = "";
            OnPropertyChanged(nameof(DraftHelp));
        }
    }
    public string StepNameText { get => _stepName; set => SetStep(ref _stepName, value ?? "", nameof(StepNameText)); }
    public string UnitText { get => _unit; set => SetStep(ref _unit, value ?? "", nameof(UnitText)); }
    public string AddressText { get => _address; set => SetStep(ref _address, value ?? "", nameof(AddressText)); }
    public string ValueText { get => _value; set => SetStep(ref _value, value ?? "", nameof(ValueText)); }
    public string ScaleText { get => _scale; set => SetStep(ref _scale, value ?? "", nameof(ScaleText)); }
    public string EngineeringUnitText { get => _engineeringUnit; set => SetStep(ref _engineeringUnit, value ?? "", nameof(EngineeringUnitText)); }
    public string ValueNamesText { get => _valueNames; set => SetStep(ref _valueNames, value ?? "", nameof(ValueNamesText)); }
    public string TimeoutText { get => _timeout; set => SetStep(ref _timeout, value ?? "", nameof(TimeoutText)); }
    public string StatusAddressText { get => _statusAddress; set => SetStep(ref _statusAddress, value ?? "", nameof(StatusAddressText)); }
    public string MaskText { get => _mask; set => SetStep(ref _mask, value ?? "", nameof(MaskText)); }
    public string ExpectedText { get => _expected; set => SetStep(ref _expected, value ?? "", nameof(ExpectedText)); }
    public string VerificationTimeoutText { get => _verificationTimeout; set => SetStep(ref _verificationTimeout, value ?? "", nameof(VerificationTimeoutText)); }
    public string VerificationIntervalText { get => _verificationInterval; set => SetStep(ref _verificationInterval, value ?? "", nameof(VerificationIntervalText)); }
    public int AreaIndex { get => _areaIndex; set => SetStep(ref _areaIndex, value, nameof(AreaIndex)); }
    public int FormatIndex { get => _formatIndex; set => SetStep(ref _formatIndex, value, nameof(FormatIndex)); }
    public int VerificationIndex { get => _verificationIndex; set => SetStep(ref _verificationIndex, value, nameof(VerificationIndex)); }
    public int StatusFunctionIndex { get => _statusFunctionIndex; set => SetStep(ref _statusFunctionIndex, value, nameof(StatusFunctionIndex)); }
    public bool SwapBytes { get => _swapBytes; set => SetStep(ref _swapBytes, value, nameof(SwapBytes)); }
    public bool SwapWords { get => _swapWords; set => SetStep(ref _swapWords, value, nameof(SwapWords)); }

    private void SetStep<TValue>(ref TValue field, TValue value, string property)
    {
        if (!SetProperty(ref field, value, property)) return;
        if (!_loading) { _stepDirty = true; Error = ""; }
        NotifyStepPresentation();
    }
    private void NotifyStepPresentation()
    {
        foreach (var name in new[] { nameof(DraftHelp), nameof(StatusVisibility), nameof(VerificationHelp), nameof(IsRegister),
            nameof(CanEditFormat), nameof(CanEditScale), nameof(CanSwapBytes), nameof(CanSwapWords), nameof(Preview), nameof(InterpretationSummary) }) OnPropertyChanged(name);
    }

    public string Preview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ValueText)) return "";
            try
            {
                var step = ReadStepDraft();
                var words = string.Join(" ", step.EncodeValues().Select(value => $"0x{value:X4}"));
                var packet = string.Join(" ", step.BuildPacket().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
                return UiText.Format("ModbusCommandPreview", "Wire words: {0}", words) + "\nHEX: " + packet;
            }
            catch (ArgumentException ex) { return ex.Message; }
        }
    }

    public void NewCommand()
    {
        if (_loading || _navigating) return;
        _navigating = true;
        try { if (CommitNavigationDraft()) ResetCommandDraft(); }
        finally { _navigating = false; }
    }
    private void ResetCommandDraft()
    {
        _loading = true;
        try
        {
            SetSelectedCommand(null);
            _commandId = Guid.NewGuid().ToString("N");
            CommandNameText = UiText.Format("ModbusCommandNewName", "Command {0}", Items.Count + 1);
            Steps.Clear();
            SetSelectedStep(null);
            ResetStepFields();
            Error = "";
        }
        finally { _loading = false; _commandDirty = false; _stepDirty = false; NotifyStepPresentation(); }
    }
    private void LoadCommand(ModbusCommandDefinition? command, int stepIndex = 0)
    {
        _loading = true;
        try
        {
            _commandId = command?.Id ?? "";
            CommandNameText = command?.Name ?? "";
            SetSelectedStep(null);
            Steps.Clear();
            if (command?.Steps is null) Steps.Add(new(null));
            else foreach (var step in command.Steps) Steps.Add(new(step?.Clone()));
            if (Steps.Count > 0)
            {
                var selected = Steps[Math.Clamp(stepIndex, 0, Steps.Count - 1)];
                SetSelectedStep(selected);
                LoadStepFields(selected.Definition);
            }
            else ResetStepFields();
            Error = command is null || command.HasInvalidMetadata
                ? T("InvalidCommand", "This saved command is invalid. Correct its definition or remove it.") : "";
        }
        finally { _loading = false; _commandDirty = false; _stepDirty = false; NotifyStepPresentation(); }
    }
    public void NewStep()
    {
        if (_loading || _navigating) return;
        _navigating = true;
        try { if (CommitStepNavigationDraft()) ResetStepDraft(); }
        finally { _navigating = false; }
    }
    private void ResetStepDraft()
    {
        _loading = true;
        try { SetSelectedStep(null); ResetStepFields(); Error = ""; }
        finally { _loading = false; _stepDirty = false; NotifyStepPresentation(); }
    }
    private void ResetStepFields()
    {
        StepNameText = UiText.Format("ModbusCommandNewStepName", "Step {0}", Steps.Count + 1);
        UnitText = "1"; AreaIndex = 1; AddressText = "0"; FormatIndex = 0; ValueText = ""; ScaleText = "1";
        _itemName = ""; EngineeringUnitText = ""; ValueNamesText = ""; SwapBytes = false; SwapWords = false;
        TimeoutText = "1000"; VerificationIndex = 0; StatusFunctionIndex = 2; StatusAddressText = "0";
        MaskText = "0xFFFF"; ExpectedText = "0"; VerificationTimeoutText = "3000"; VerificationIntervalText = "100";
        IsAdvancedExpanded = false;
    }
    private void LoadStep(ModbusCommandStep? step)
    {
        _loading = true;
        try { LoadStepFields(step); Error = step is null || step.HasInvalidMetadata ? T("InvalidStep", "This saved step is invalid. Correct its fields or remove it.") : ""; }
        finally { _loading = false; _stepDirty = false; NotifyStepPresentation(); }
    }
    private void LoadStepFields(ModbusCommandStep? step)
    {
        var item = step?.Item;
        var verification = step?.Verification;
        StepNameText = step?.Name ?? "";
        UnitText = step is null ? "" : step.UnitId.ToString(CultureInfo.InvariantCulture);
        AreaIndex = step?.Function switch { 1 => 0, 3 => 1, _ => -1 };
        AddressText = item is null ? "" : item.Address.ToString(CultureInfo.InvariantCulture);
        FormatIndex = item is not null && Enum.IsDefined(item.ValueType) ? (int)item.ValueType : -1;
        ValueText = step?.Value ?? "";
        ScaleText = item is null ? "" : item.Scale.ToString("G17", CultureInfo.CurrentCulture);
        _itemName = item?.Name ?? "";
        EngineeringUnitText = item?.Unit ?? "";
        ValueNamesText = item?.ValueNames is null ? "?=" : string.Join(Environment.NewLine,
            item.ValueNames.Select(value => value is null ? "?=" : $"{value.Value.ToString(CultureInfo.InvariantCulture)}={value.Name}"));
        SwapBytes = item?.SwapBytes ?? false;
        SwapWords = item?.SwapWords ?? false;
        TimeoutText = step is null ? "" : step.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        VerificationIndex = verification is not null && Enum.IsDefined(verification.Mode) ? (int)verification.Mode : -1;
        StatusFunctionIndex = verification?.Function is >= 1 and <= 4 ? verification.Function - 1 : -1;
        StatusAddressText = verification is null ? "" : verification.Address.ToString(CultureInfo.InvariantCulture);
        MaskText = verification is null ? "" : $"0x{verification.Mask:X4}";
        ExpectedText = verification is null ? "" : $"0x{verification.ExpectedValue:X4}";
        VerificationTimeoutText = verification is null ? "" : verification.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        VerificationIntervalText = verification is null ? "" : verification.IntervalMs.ToString(CultureInfo.InvariantCulture);
        if (item?.Access != ModbusItemAccess.ReadWrite)
            AreaIndex = -1; // Reaffirm an explicit write target instead of granting access on a rename.
        if (step?.HasInvalidMetadata == true)
        {
            // A malformed imported scalar may otherwise look like a valid
            // default (for example a false byte-swap flag). Require explicit
            // replacement of the write interpretation before clearing its marker.
            AreaIndex = -1;
            FormatIndex = -1;
            ValueText = "";
        }
        if (verification?.HasInvalidMetadata == true)
        {
            // ushort cannot carry an invalid sentinel. In particular a missing
            // ExpectedValue must never silently become a valid zero condition.
            VerificationIndex = -1;
            StatusAddressText = "";
            MaskText = "";
            ExpectedText = "";
            VerificationTimeoutText = "";
            VerificationIntervalText = "";
        }
        IsAdvancedExpanded = FormatIndex != 0 || ScaleText != "1" || EngineeringUnitText.Length > 0 ||
            ValueNamesText.Length > 0 || SwapBytes || SwapWords || TimeoutText != "1000";
    }

    private ModbusCommandStep ReadStepDraft()
    {
        if (!ModbusRtuViewModel.TryNumber(UnitText, out var unit) || !ModbusRtuViewModel.TryNumber(AddressText, out var address) ||
            AreaIndex is < 0 or > 1 || FormatIndex is < 0 or > 4 || VerificationIndex is < 0 or > 2 ||
            !int.TryParse(TimeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout))
            throw new ArgumentException(T("InvalidWriteFields", "Enter a complete unit, write area, address, type, timeout and result-check mode. Addresses also accept 0x HEX."));
        if (!double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.CurrentCulture, out var scale) &&
            !double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
            throw new ArgumentException(T("InvalidScale", "Enter a finite nonzero multiplier, for example 1 or 0.1."));
        var verification = new ModbusCommandVerification { Mode = (ModbusCommandVerificationMode)VerificationIndex };
        if (verification.Mode == ModbusCommandVerificationMode.Status)
        {
            if (StatusFunctionIndex is < 0 or > 3 || !ModbusRtuViewModel.TryNumber(StatusAddressText, out var statusAddress) ||
                !ModbusRtuViewModel.TryNumber(MaskText, out var mask) || mask > ushort.MaxValue ||
                !ModbusRtuViewModel.TryNumber(ExpectedText, out var expected) || expected > ushort.MaxValue ||
                !int.TryParse(VerificationTimeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var verificationTimeout) ||
                !int.TryParse(VerificationIntervalText, NumberStyles.None, CultureInfo.InvariantCulture, out var verificationInterval))
                throw new ArgumentException(T("InvalidStatusFields", "Enter a complete status address, mask, expected raw value and check timings."));
            verification.Function = (byte)(StatusFunctionIndex + 1); verification.Address = statusAddress;
            verification.Mask = (ushort)mask; verification.ExpectedValue = (ushort)expected;
            verification.TimeoutMs = verificationTimeout; verification.IntervalMs = verificationInterval;
        }
        var step = new ModbusCommandStep
        {
            Name = StepNameText.Trim(), UnitId = unit, Function = AreaIndex == 0 ? (byte)1 : (byte)3,
            Item = new() { Address = address, Name = _itemName, ValueType = (ModbusValueType)FormatIndex, Scale = scale,
                Unit = EngineeringUnitText.Trim(), SwapBytes = SwapBytes, SwapWords = SwapWords,
                Access = ModbusItemAccess.ReadWrite, ValueNames = ParseValueNames() },
            Value = ValueText, TimeoutMs = timeout, Verification = verification
        };
        step.Validate();
        return step;
    }
    private List<ModbusValueName> ParseValueNames()
    {
        var names = new List<ModbusValueName>();
        if (ValueNamesText.Length > 20000) throw new ArgumentException(T("InvalidValueNames", "Use at most 128 value names, one integer = name per line."));
        foreach (var line in ValueNamesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new ArgumentException(T("InvalidValueNames", "Use at most 128 value names, one integer = name per line."));
            var text = line[..equals].Trim();
            var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!long.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException(T("InvalidValueNames", "Use at most 128 value names, one integer = name per line."));
            names.Add(new() { Value = value, Name = line[(equals + 1)..].Trim() });
            if (names.Count > 128) throw new ArgumentException(T("InvalidValueNames", "Use at most 128 value names, one integer = name per line."));
        }
        return names;
    }

    public bool SaveStep()
    {
        try
        {
            var step = ReadStepDraft();
            var index = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
            if (index < 0 && Steps.Count >= ModbusCommandDefinition.MaximumSteps)
                throw new ArgumentException(T("TooManySteps", "At most 32 steps are allowed in a command."));
            var replacement = new ModbusCommandStepEditorEntry(step);
            _loading = true;
            try
            {
                if (index < 0) Steps.Add(replacement); else Steps[index] = replacement;
                SetSelectedStep(replacement);
                LoadStepFields(step);
            }
            finally { _loading = false; }
            _stepDirty = false;
            _commandDirty = true;
            Error = "";
            NotifyStepPresentation();
            return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }
    public bool SaveCommand()
    {
        if (_stepDirty && !SaveStep()) return false;
        try
        {
            var duplicateId = Items.Any(entry => !ReferenceEquals(entry, SelectedCommand) && entry.Definition?.Id == _commandId);
            var validId = !string.IsNullOrWhiteSpace(_commandId) && _commandId.Length <= ModbusCommandDefinition.MaximumIdLength &&
                _commandId == _commandId.Trim() && !_commandId.Any(char.IsControl) && !duplicateId;
            var command = new ModbusCommandDefinition { Id = validId ? _commandId : Guid.NewGuid().ToString("N"), Name = CommandNameText.Trim(), Steps = Steps.Select(entry => entry.Definition?.Clone()!).ToList() };
            command.Validate();
            var index = SelectedCommand is null ? -1 : Items.IndexOf(SelectedCommand);
            if (index < 0 && Items.Count >= ModbusCommandSettings.MaximumCommands)
                throw new ArgumentException(T("TooManyCommands", "At most 32 commands are allowed."));
            var stepIndex = SelectedStep is null ? 0 : Steps.IndexOf(SelectedStep);
            var replacement = new ModbusCommandEditorEntry(command);
            _loading = true;
            try
            {
                if (index < 0) Items.Add(replacement); else Items[index] = replacement;
                SetSelectedCommand(replacement);
                _selectedCommandId = command.Id;
            }
            finally { _loading = false; }
            _invalidMetadata = false;
            LoadCommand(command, stepIndex);
            return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }
    public bool DuplicateCommand()
    {
        if ((_commandDirty || _stepDirty) && !SaveCommand()) return false;
        if (SelectedCommand?.Definition is not { } selected) return false;
        try
        {
            selected.Validate();
            if (Items.Count >= ModbusCommandSettings.MaximumCommands)
                throw new ArgumentException(T("TooManyCommands", "At most 32 commands are allowed."));
            var copy = selected.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            var suffix = T("CopySuffix", " copy");
            copy.Name = copy.Name.Length + suffix.Length <= ModbusCommandDefinition.MaximumNameLength
                ? copy.Name + suffix : UiText.Format("ModbusCommandNewName", "Command {0}", Items.Count + 1);
            var entry = new ModbusCommandEditorEntry(copy);
            _loading = true;
            try { Items.Add(entry); SetSelectedCommand(entry); _selectedCommandId = copy.Id; }
            finally { _loading = false; }
            _invalidMetadata = false;
            LoadCommand(copy);
            return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }
    public bool DuplicateStep()
    {
        if (_stepDirty && !SaveStep()) return false;
        if (SelectedStep?.Definition is not { } selected) return false;
        try
        {
            selected.Validate();
            if (Steps.Count >= ModbusCommandDefinition.MaximumSteps)
                throw new ArgumentException(T("TooManySteps", "At most 32 steps are allowed in a command."));
            var entry = new ModbusCommandStepEditorEntry(selected.Clone());
            _loading = true;
            try { Steps.Insert(Steps.IndexOf(SelectedStep!) + 1, entry); SetSelectedStep(entry); LoadStepFields(entry.Definition); }
            finally { _loading = false; }
            _stepDirty = false; _commandDirty = true; Error = ""; NotifyStepPresentation();
            return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }
    public void RemoveCommand()
    {
        if (SelectedCommand is not { } selected) return;
        _loading = true;
        try { Items.Remove(selected); SetSelectedCommand(null); }
        finally { _loading = false; }
        _invalidMetadata = false;
        ResetCommandDraft();
        if (Items.FirstOrDefault() is { } next) SelectedCommand = next;
        else _selectedCommandId = null;
    }
    public void ClearCommands()
    {
        _loading = true;
        try { Items.Clear(); SetSelectedCommand(null); _selectedCommandId = null; }
        finally { _loading = false; }
        _invalidMetadata = false;
        ResetCommandDraft();
    }
    public void RemoveStep()
    {
        if (SelectedStep is not { } selected) return;
        var index = Steps.IndexOf(selected);
        _loading = true;
        try
        {
            Steps.Remove(selected);
            SetSelectedStep(null);
            ResetStepFields();
            if (Steps.Count > 0)
            {
                SetSelectedStep(Steps[Math.Min(index, Steps.Count - 1)]);
                LoadStepFields(SelectedStep!.Definition);
            }
            Error = "";
        }
        finally { _loading = false; _stepDirty = false; }
        _commandDirty = true;
        NotifyStepPresentation();
    }
    public bool MoveStep(int offset)
    {
        if (offset is not (-1 or 1)) return false;
        if (_stepDirty && !SaveStep()) return false;
        if (SelectedStep is not { } selected) return false;
        var index = Steps.IndexOf(selected);
        var next = index + offset;
        if (next < 0 || next >= Steps.Count) return false;
        _loading = true;
        try { Steps.Move(index, next); SetSelectedStep(selected); }
        finally { _loading = false; }
        _commandDirty = true;
        NotifyStepPresentation();
        return true;
    }

    public ModbusCommandSettings CreateSettings()
    {
        if ((_commandDirty || _stepDirty) && !SaveCommand()) throw new ArgumentException(Error);
        var settings = new ModbusCommandSettings
        {
            Commands = Items.Select(entry => entry.Definition?.Clone()!).ToList(), SelectedCommandId = _selectedCommandId,
            HasInvalidMetadata = _invalidMetadata
        };
        settings.Validate();
        return settings;
    }
    public bool TryApply(out ModbusCommandSettings settings)
    {
        settings = new();
        try { settings = CreateSettings(); Error = ""; return true; }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }
}

public sealed class ModbusCommandEditorEntry(ModbusCommandDefinition? definition)
{
    public ModbusCommandDefinition? Definition { get; } = definition;
    public string DisplayName => Definition is null ? UiText.Get("ModbusCommandInvalidShort", "Invalid saved command")
        : $"{(string.IsNullOrWhiteSpace(Definition.Name) ? "—" : Definition.Name)} · {Definition.Steps?.Count ?? 0}";
}
public sealed class ModbusCommandStepEditorEntry(ModbusCommandStep? definition)
{
    public ModbusCommandStep? Definition { get; } = definition;
    public string DisplayName => Definition is not { Item: { } item } step ? UiText.Get("ModbusCommandInvalidStepShort", "Invalid saved step")
        : $"{(string.IsNullOrWhiteSpace(step.Name) ? "—" : step.Name)} · ID {step.UnitId} · {step.Function:00} · {item.Address} ← {step.Value}";
}
