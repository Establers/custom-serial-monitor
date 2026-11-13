using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public TerminalEnterMode TerminalEnter
    {
        get => _currentUiSettings.TerminalEnter;
        set
        {
            if (!Enum.IsDefined(value) || TerminalEnter == value) return;
            _currentUiSettings.TerminalEnter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TerminalEnterMenuText));
        }
    }

    public TerminalBackspaceMode TerminalBackspace
    {
        get => _currentUiSettings.TerminalBackspace;
        set
        {
            if (!Enum.IsDefined(value) || TerminalBackspace == value) return;
            _currentUiSettings.TerminalBackspace = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TerminalBackspaceMenuText));
        }
    }

    public TerminalReceiveNewlineMode TerminalReceiveNewline
    {
        get => _currentUiSettings.TerminalReceiveNewline;
        set
        {
            if (!Enum.IsDefined(value) || TerminalReceiveNewline == value) return;
            _currentUiSettings.TerminalReceiveNewline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TerminalReceiveNewlineMenuText));
        }
    }

    public string TerminalEnterMenuText => UiText.Format("TerminalEnterMenuText", "Enter: {0}",
        TerminalEnter switch { TerminalEnterMode.Lf => "LF", TerminalEnterMode.CrLf => "CRLF", _ => "CR" });

    public string TerminalBackspaceMenuText => UiText.Format("TerminalBackspaceMenuText", "Backspace: {0}",
        TerminalBackspace == TerminalBackspaceMode.Bs ? "BS" : "DEL");

    public string TerminalReceiveNewlineMenuText => UiText.Format("TerminalReceiveNewlineMenuText", "Receive newline: {0}",
        TerminalReceiveNewline switch
        {
            TerminalReceiveNewlineMode.Lf => "LF → CRLF",
            TerminalReceiveNewlineMode.Cr => "CR → CRLF",
            TerminalReceiveNewlineMode.Auto => UiText.Get("TerminalReceiveNewlineAuto/Text", "Auto (CR/LF)"),
            _ => UiText.Get("TerminalReceiveNewlinePreserve/Text", "Original")
        });

    private void NotifyTerminalConsoleSettingsChanged()
    {
        OnPropertyChanged(nameof(TerminalDirectInputLocalEcho));
        OnPropertyChanged(nameof(TerminalEnter));
        OnPropertyChanged(nameof(TerminalBackspace));
        OnPropertyChanged(nameof(TerminalReceiveNewline));
        OnPropertyChanged(nameof(TerminalEnterMenuText));
        OnPropertyChanged(nameof(TerminalBackspaceMenuText));
        OnPropertyChanged(nameof(TerminalReceiveNewlineMenuText));
    }
}
