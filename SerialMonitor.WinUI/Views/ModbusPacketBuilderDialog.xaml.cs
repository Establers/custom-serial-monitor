using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusPacketBuilderDialog : ContentDialog
{
    private CancellationTokenSource? _copyCancellation;
    public ModbusPacketBuilderViewModel Model { get; } = new();
    public ModbusPacketBuilderDialog()
    {
        InitializeComponent();
        DataContext = Model;
        Closed += (_, _) => _copyCancellation?.Cancel();
        Unloaded += (_, _) => _copyCancellation?.Cancel();
        Model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(Model.Preview)) CopyStatus.Text = string.Empty;
        };
    }

    private async void Copy_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_copyCancellation is not null) return;
        Model.Refresh();
        if (!Model.CanUsePacket) return;
        var preview = Model.Preview;
        using var cancellation = new CancellationTokenSource();
        _copyCancellation = cancellation;
        try
        {
            await ClipboardTextCopy.CopyAsync(preview, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (Model.Preview == preview)
                CopyStatus.Text = UiText.Get("ModbusBuilderCopied", "HEX copied.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("ModbusPacketBuilder.Copy", ex);
            if (!cancellation.IsCancellationRequested && Model.Preview == preview)
                CopyStatus.Text = UiText.Get("ModbusBuilderCopyFailed", "Could not copy HEX. Select and copy the preview text.");
        }
        finally { _copyCancellation = null; }
    }
}
