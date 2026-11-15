using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusSensorValueDialog : ContentDialog
{
    private bool _applying;
    public ModbusSensorValueDialog() => InitializeComponent();
    public string Value { get => ValueEditor.Text; set => ValueEditor.Text = value; }
    public Func<string, Task> ApplyValue { get; set; } = _ => Task.FromException(new InvalidOperationException("항목을 다시 선택하세요."));
    private async void Apply_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_applying) { args.Cancel = true; return; }
        var deferral = args.GetDeferral();
        _applying = true; IsPrimaryButtonEnabled = false; ValueEditor.IsReadOnly = true;
        PrimaryButtonText = "적용 중…"; ErrorText.Visibility = Visibility.Collapsed;
        try { await ApplyValue(Value); }
        catch (Exception ex) { args.Cancel = true; ErrorText.Text = ex.Message; ErrorText.Visibility = Visibility.Visible; }
        finally { _applying = false; IsPrimaryButtonEnabled = true; ValueEditor.IsReadOnly = false; PrimaryButtonText = "값 적용"; deferral.Complete(); }
    }
    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_applying) args.Cancel = true;
    }
}
