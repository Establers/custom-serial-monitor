namespace SerialMonitor.WinUI.ViewModels;

// Keep each selection label on the data item before WinUI creates its container.
// Lazy DataContext changes must not leave a cached empty ComboBoxItem.Content.
public sealed class ProtocolViewOption(string label, bool isEnabled) : ViewModelBase
{
    private bool _isEnabled = isEnabled;
    public string Label { get; } = label;
    public bool IsEnabled { get => _isEnabled; internal set => SetProperty(ref _isEnabled, value); }
}
