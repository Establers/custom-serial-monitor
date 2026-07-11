namespace SerialMonitor.WinUI.Models;

public sealed class TxCommand
{
    public TxCommand()
    {
    }

    public TxCommand(string name, string commandText)
    {
        Name = name;
        CommandText = commandText;
    }

    public string Name { get; set; } = string.Empty;

    public string CommandText { get; set; } = string.Empty;

    public TxLineEndingMode? LineEndingMode { get; set; }

    public string? OptionalShortcut { get; set; }

    public string SendToolTip
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return "Send command";
            }

            return string.IsNullOrWhiteSpace(OptionalShortcut)
                ? $"Send command: {Name}"
                : $"Send command: {Name} ({OptionalShortcut.Trim()})";
        }
    }
}
