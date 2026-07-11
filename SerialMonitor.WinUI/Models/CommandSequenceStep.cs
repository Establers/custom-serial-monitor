using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SerialMonitor.WinUI.Models;

public sealed class CommandSequenceStep : INotifyPropertyChanged
{
    private int _stepNumber;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? Name { get; set; }

    public string CommandText { get; set; } = string.Empty;

    public TxLineEndingMode? LineEndingMode { get; set; }

    public int DelayAfterMs { get; set; } = 300;

    public string? Comment { get; set; }

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
    public string RowToolTip => $"{StepNumberText} | {CommandText} | {LineEndingHelpText} | Delay {DelayText}";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
