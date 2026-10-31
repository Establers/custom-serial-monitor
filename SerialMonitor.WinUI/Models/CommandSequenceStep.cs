using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.WinUI.Models;

public enum CommandSequenceStepKind { Tx, RxCheck }

public sealed class CommandSequenceStep : INotifyPropertyChanged
{
    private int _stepNumber;
    private bool _isEnabled = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Disabled steps remain in the definition but are omitted from every run.
    // Initializing to true preserves execution of legacy profiles and files.
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RowToolTip));
        }
    }

    // Optional label used in GUI progress/errors and CLI/MCP step results; never transmitted.
    public string? Name { get; set; }

    private string _commandText = string.Empty;
    private string? _rxPattern;
    [JsonIgnore]
    public string CommandText
    {
        get => Kind == CommandSequenceStepKind.RxCheck ? _rxPattern ?? _commandText : _commandText;
        set { if (Kind == CommandSequenceStepKind.RxCheck) _rxPattern = value; else _commandText = value; }
    }

    // An older application that ignores Kind must never transmit an RX pattern.
    [JsonPropertyName("CommandText")]
    public string StoredCommandText { get => Kind == CommandSequenceStepKind.RxCheck ? "" : _commandText; set => _commandText = value; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RxPattern { get => Kind == CommandSequenceStepKind.RxCheck ? CommandText : null; set => _rxPattern = value; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CommandSequenceStepKind Kind { get; set; }

    [JsonIgnore]
    public string ModeText => TxMode switch { TxSendMode.Hex => "HEX", TxSendMode.Terminal => "TERM", _ => "Global" };

    [JsonIgnore]
    public string KindText => Kind == CommandSequenceStepKind.RxCheck ? "RX Check" : "TX";

    public TxLineEndingMode? LineEndingMode { get; set; }

    public int DelayAfterMs { get; set; } = 300;

    // Reference note shown in the row tooltip and preserved in profile/sequence JSON.
    public string? Comment { get; set; }

    // Null keeps legacy behavior: use the main TX mode captured at run start.
    public TxSendMode? TxMode { get; set; }

    // Capture and matching metadata is used by CLI/MCP runs, not ordinary GUI Run.
    public int CaptureMs { get; set; } = 2000;

    public AutomationResponseExpectation? Expect { get; set; }

    // Explicit opt-in live checking. Legacy Expect/CaptureMs retain their API semantics.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SerialMonitor.Core.Sequences.ResponseCheck? ResponseCheck { get; set; }

    [JsonIgnore]
    public int StepNumber
    {
        get => _stepNumber;
        set
        {
            if (_stepNumber == value)
            {
                return;
            }

            _stepNumber = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StepNumberText));
            OnPropertyChanged(nameof(RowToolTip));
        }
    }

    [JsonIgnore]
    public string StepNumberText => StepNumber > 0
        ? StepNumber.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? CommandText
        : Name.Trim();

    public string LineEndingText => Kind == CommandSequenceStepKind.RxCheck ? "—" : LineEndingMode switch
    {
        TxLineEndingMode.None => "None",
        TxLineEndingMode.Cr => "CR",
        TxLineEndingMode.Lf => "LF",
        TxLineEndingMode.Crlf => "CRLF",
        _ => "Global"
    };

    [JsonIgnore]
    public string LineEndingHelpText => LineEndingMode switch
    {
        TxLineEndingMode.None => "None: send command without a line ending.",
        TxLineEndingMode.Cr => @"CR: append \r.",
        TxLineEndingMode.Lf => @"LF: append \n.",
        TxLineEndingMode.Crlf => @"CRLF: append \r\n.",
        _ => "Global: use the TX ending selected in the main TX area."
    };

    public string DelayText => $"{DelayAfterMs} ms";

    public string CommentText => string.IsNullOrWhiteSpace(Comment) ? string.Empty : Comment.Trim();

    [JsonIgnore]
    public string RowToolTip
    {
        get
        {
            var details = $"{StepNumberText} | {KindText} | {CommandText} | {TxMode?.ToString() ?? "Global"} | {DelayText}";
            details += Kind == CommandSequenceStepKind.RxCheck
                ? "\nRX Check: waits the full duration, including after a match. Global inherits the preceding TX mode."
                : $" | {LineEndingHelpText} | {AutomationHelpText}";
            if (!IsEnabled) details = $"Disabled: skipped during execution.{Environment.NewLine}{details}";
            if (ResponseCheck is { } check) details += $"{Environment.NewLine}Response ({check.Kind}, {check.TimeoutMs} ms): {(check.Pattern.Length > 80 ? check.Pattern[..80] + "…" : check.Pattern)}";
            if (!string.IsNullOrWhiteSpace(Name)) details = $"Step name: {Name.Trim()}{Environment.NewLine}{details}";
            if (!string.IsNullOrWhiteSpace(Comment)) details += $"{Environment.NewLine}Comment: {CommentText}";
            return details;
        }
    }

    [JsonIgnore]
    public string AutomationHelpText => $"CLI/MCP only: collect RX {CaptureMs} ms; " + (Expect is null
        ? "no legacy response match. Add an RX Check step for GUI checking."
        : $"literal {Expect.Kind} match '{(Expect.Value?.Length > 80 ? Expect.Value[..80] + "…" : Expect.Value)}'. These legacy fields do not enable live GUI checks.");

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
