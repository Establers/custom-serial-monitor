using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusPacketBuilderDialog : ContentDialog
{
    private readonly ClipboardCopySession _copySession = new();
    public ModbusPacketBuilderViewModel Model { get; } = new();
    public ModbusPacketBuilderDialog()
    {
        InitializeComponent();
        DataContext = Model;
        Closed += (_, _) => _copySession.Cancel();
        Unloaded += (_, _) => _copySession.Cancel();
        Model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(Model.Preview)) return;
            _copySession.PreviewChanged(Model.Preview);
            CopyStatus.Text = string.Empty;
        };
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // Keep the native button row aligned with the editor's scrollbar gutter.
        if (GetTemplateChild("CommandSpace") is FrameworkElement commandSpace)
            commandSpace.Margin = EditorPanel.Margin;
    }

    private async void Copy_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        Model.Refresh();
        if (!Model.CanUsePacket) return;
        using var request = _copySession.TryBegin(Model.Preview);
        if (request is null) return;
        try
        {
            await ClipboardTextCopy.CopyAsync(request.Preview, request.Token);
            if (_copySession.IsCurrent(request))
                CopyStatus.Text = UiText.Get("ModbusBuilderCopied", "HEX copied.");
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("ModbusPacketBuilder.Copy", ex);
            if (_copySession.IsCurrent(request))
                CopyStatus.Text = UiText.Get("ModbusBuilderCopyFailed", "Could not copy HEX. Select and copy the preview text.");
        }
        finally { _copySession.Complete(request); }
    }
}
