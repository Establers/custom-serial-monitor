using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.WinUI.Models;

public sealed class CommandSequenceStep : INotifyPropertyChanged
{
    private int _stepNumber;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Optional label used in GUI progress/errors and CLI/MCP step results; never transmitted.
    public string? Name { get; set; }

    public string CommandText { get; set; } = string.Empty;

    public TxLineEndingMode? LineEndingMode { get; set; }

    public int DelayAfterMs { get; set; } = 300;

    // Reference note shown in the row tooltip and preserved in profile/sequence JSON.
    public string? Comment { get; set; }

    // Null keeps legacy behavior: use the main TX mode captured at run start.
    public TxSendMode? TxMode { get; set; }

    // Capture and matching metadata is used by CLI/MCP runs, not ordinary GUI Run.
    public int CaptureMs { get; set; } = 2000;

    public AutomationResponseExpectation? Expect { get; set; }

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

    public string LineEndingText => LineEndingMode switch
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
            var details = $"{StepNumberText} | {CommandText} | TX {TxMode?.ToString() ?? "Global"} | {LineEndingHelpText} | Delay {DelayText} | {AutomationHelpText}";
            if (!string.IsNullOrWhiteSpace(Name)) details = $"Step name: {Name.Trim()}{Environment.NewLine}{details}";
            if (!string.IsNullOrWhiteSpace(Comment)) details += $"{Environment.NewLine}Comment: {CommentText}";
            return details;
        }
    }

    [JsonIgnore]
    public string AutomationHelpText => $"CLI/MCP only: collect RX {CaptureMs} ms; " + (Expect is null
        ? "no response match. GUI Run uses TX mode and delay only."
        : $"literal {Expect.Kind} match '{(Expect.Value?.Length > 80 ? Expect.Value[..80] + "…" : Expect.Value)}'. GUI Run does not capture or match.");

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
