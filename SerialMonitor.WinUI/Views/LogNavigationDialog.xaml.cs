using Microsoft.UI.Xaml.Controls;

namespace SerialMonitor.WinUI.Views;

public sealed partial class LogNavigationDialog : ContentDialog
{
    public LogNavigationDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (Content is LogNavigationView navigation)
                navigation.SetPrimaryActionFocusTarget(GetTemplateChild("PrimaryButton") as Control);
        };
        Closed += (_, _) =>
        {
            if (Content is LogNavigationView navigation) navigation.SetPrimaryActionFocusTarget(null);
        };
    }
}
