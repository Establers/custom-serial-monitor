using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SerialMonitor.WinUI.Views;

public sealed partial class LogRuleTransferDialog : ContentDialog
{
    private readonly LogRule[]? _exportRules;
    private readonly LogRule? _selected;
    private CancellationTokenSource? _validation;
    private bool _ready;
    private bool _closed;
    public IReadOnlyList<LogRule>? ImportedRules { get; private set; }

    public LogRuleTransferDialog(IEnumerable<LogRule>? exportRules = null, LogRule? selected = null)
    {
        InitializeComponent();
        _exportRules = exportRules?.Select(r => r.Clone()).ToArray();
        _selected = selected?.Clone();
        var export = _exportRules is not null;
        Title = UiText.Get(export ? "RulesExportTitle" : "RulesImportTitle", export ? "Export rules" : "Import rules");
        PrimaryButtonText = UiText.Get(export ? "RulesCopy" : "RulesImport", export ? "Copy" : "Import");
        CloseButtonText = UiText.Get("RulesClose", "Close");
        HelpText.Text = UiText.Get(export ? "RulesExportHelp" : "RulesImportHelp", export
            ? "Copy the text and share it in a message. Paste it into Import rules on another PC."
            : "Paste exported rule text. Existing rules stay; exact duplicates are skipped. Rules linked to a sequence are added disabled so you can check the local sequence first.");
        SelectedOnlyBox.Content = UiText.Get("RulesSelectedOnly", "Selected rule only");
        SelectedOnlyBox.Visibility = export && selected is not null ? Visibility.Visible : Visibility.Collapsed;
        TransferText.IsReadOnly = export;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TransferText,
            UiText.Get("RulesTransferText", "Rule sharing text"));
        IsPrimaryButtonEnabled = false;
        _ready = true;
        if (export) _ = UpdateExportAsync();
    }

    private async void SelectedOnly_Click(object sender, RoutedEventArgs args) => await UpdateExportAsync();

    private async Task UpdateExportAsync()
    {
        _validation?.Cancel();
        var validation = new CancellationTokenSource();
        _validation = validation;
        IsPrimaryButtonEnabled = false;
        try
        {
            var rules = SelectedOnlyBox.IsChecked == true && _selected is not null ? new[] { _selected } : _exportRules!;
            var text = await Task.Run(() => LogRuleTransfer.Encode(rules, validation.Token), validation.Token);
            if (validation.IsCancellationRequested || _closed) return;
            TransferText.Text = text;
            StatusText.Text = UiText.Format("RulesShareCount", "{0} rules · settings only; sequences are not included.", rules.Length);
            IsPrimaryButtonEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!validation.IsCancellationRequested && !_closed)
            { TransferText.Text = ""; StatusText.Text = ex.Message; IsPrimaryButtonEnabled = false; }
        }
        finally
        {
            if (ReferenceEquals(_validation, validation)) _validation = null;
            validation.Dispose();
        }
    }

    private async void TransferText_Changed(object sender, TextChangedEventArgs args)
    {
        if (!_ready || _exportRules is not null || _closed) return;
        _validation?.Cancel();
        var validation = new CancellationTokenSource();
        _validation = validation;
        ImportedRules = null;
        IsPrimaryButtonEnabled = false;
        StatusText.Text = "";
        var text = TransferText.Text;
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            await Task.Delay(200, validation.Token);
            var rules = await Task.Run(() => LogRuleTransfer.Decode(text, validation.Token), validation.Token);
            if (validation.IsCancellationRequested || _closed) return;
            ImportedRules = rules;
            StatusText.Text = UiText.Format("RulesImportCount", "{0} rules ready to add. Click Import to apply.", rules.Count);
            IsPrimaryButtonEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ex is not FormatException) RuntimeDiagnostics.RecordError("LogRuleTransferDialog.Validation", ex);
            if (!validation.IsCancellationRequested && !_closed)
                StatusText.Text = UiText.Get("RulesInvalidText", "Cannot import this text: ") + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_validation, validation)) _validation = null;
            validation.Dispose();
        }
    }

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_exportRules is null) { args.Cancel = ImportedRules is null; return; }
        args.Cancel = true;
        try
        {
            var package = new DataPackage();
            package.SetText(TransferText.Text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            StatusText.Text = UiText.Get("RulesCopied", "Copied. Paste the text into a message or Import rules.");
        }
        catch (Exception ex) { StatusText.Text = UiText.Get("RulesCopyFailed", "Copy failed. Select the text and press Ctrl+C. ") + ex.Message; }
    }

    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _closed = true;
        _validation?.Cancel();
    }
}
