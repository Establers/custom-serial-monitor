using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusDeviceAiGuideDialog : ContentDialog
{
    private readonly ClipboardCopySession _copySession = new();
    public ModbusDeviceAiGuideDialog()
    {
        InitializeComponent();
        Closed += (_, _) => _copySession.Cancel();
        Unloaded += (_, _) => _copySession.Cancel();
    }

    // Replaceable in the native dialog fixture; production always uses the shared clipboard helper.
    internal Func<string, CancellationToken, Task> CopyTextAsync { get; set; } = ClipboardTextCopy.CopyAsync;
    private async void Copy_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        using var request = _copySession.TryBegin("device-authoring");
        if (request is null) return;
        CopyStatus.Text = "복사 중…";
        try
        {
            var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "docs", "modbus_sensor_authoring.md"), request.Token);
            await CopyTextAsync(text, request.Token);
            if (_copySession.IsCurrent(request)) CopyStatus.Text = "복사했어요. 제품의 통신 프로토콜 자료와 함께 AI 채팅에 붙여 넣으세요.";
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_copySession.IsCurrent(request)) CopyStatus.Text = "복사하지 못했어요. 다시 눌러 주세요. " + ex.Message;
        }
        finally { _copySession.Complete(request); }
    }
}
