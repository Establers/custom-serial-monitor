using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.Core.Modbus;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusDeviceEditorDialog : ContentDialog
{
    private bool _saving;
    public ModbusDeviceEditorDialog() => InitializeComponent();
    // No live binding or parsing during typing/selection. The main preview updates once on save.
    public string Json { get => JsonEditor.Text; set => JsonEditor.Text = value; }
    public Func<string, Task> Save { get; set; } = json => Task.Run(() => { ModbusSensorDefinition.Parse(json); });
    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_saving) { args.Cancel = true; return; }
        var deferral = args.GetDeferral();
        _saving = true; IsPrimaryButtonEnabled = false; JsonEditor.IsReadOnly = true;
        ErrorText.Visibility = Visibility.Collapsed;
        try { await Save(Json); }
        catch (Exception ex) { args.Cancel = true; ErrorText.Text = "저장하지 못했어요: " + ex.Message; ErrorText.Visibility = Visibility.Visible; }
        finally { _saving = false; IsPrimaryButtonEnabled = true; JsonEditor.IsReadOnly = false; deferral.Complete(); }
    }
    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_saving) args.Cancel = true;
    }
}
