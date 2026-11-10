using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusPacketBuilderDialog : ContentDialog
{
    public ModbusPacketBuilderViewModel Model { get; } = new();
    public ModbusPacketBuilderDialog()
    {
        InitializeComponent();
        DataContext = Model;
    }

    private void Use_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Model.Refresh();
        args.Cancel = !Model.CanUsePacket;
    }

    private void Copy_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        Model.Refresh();
        if (!Model.CanUsePacket) return;
        try
        {
            var data = new DataPackage();
            data.SetText(Model.Preview);
            Clipboard.SetContent(data);
            CopyStatus.Text = UiText.Get("ModbusBuilderCopied", "HEX copied.");
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("ModbusPacketBuilder.Copy", ex);
            CopyStatus.Text = UiText.Get("ModbusBuilderCopyFailed", "Could not copy HEX. Select and copy the preview text.");
        }
    }
}
