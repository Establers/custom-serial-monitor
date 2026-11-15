using System.Collections;
using System.Text;
using System.Text.Json;
using Windows.Foundation;

public static partial class StartupHook
{
    // Opt-in UI fixtures: no COM connection, profile save or clipboard mutation.
    private static async Task AuditRulesDialogs(string suffix)
    {
        var rule = New("SerialMonitor.WinUI.Models.LogRule");
        S(rule, "Name", "온도 경고 / Temperature"); S(rule, "Keyword", @"^temp=\d+$");
        S(rule, "UseRegex", true); S(rule, "UseForEvent", true); S(rule, "UseAsViewFilter", true);
        S(rule, "ForegroundColor", "Orange");
        var secondRule = New("SerialMonitor.WinUI.Models.LogRule");
        S(secondRule, "Name", "Warning"); S(secondRule, "Keyword", "[W]");
        var rules = Array.CreateInstance(rule.GetType(), 2); rules.SetValue(rule, 0); rules.SetValue(secondRule, 1);
        var sequences = Array.CreateInstance(FindType("SerialMonitor.WinUI.Models.CommandSequence"), 0);
        var text = "SMRULES:1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rules, rules.GetType())));
        foreach (var theme in new[] { "Dark", "Light" })
        {
            var editor = New("SerialMonitor.WinUI.Views.LogRuleDialog", rule,
                new[] { "Default", "Red", "Orange", "Blue" }, sequences);
            S(editor, "Title", "Edit log rule"); S(editor, "RequestedTheme", theme);
            await RuleDialog(editor, "rules-editor-" + theme + suffix, async () =>
            {
                foreach (var name in new[] { "NameBox", "KeywordBox", "ModeBox", "DirectionBox", "RegexBox", "ForegroundBox", "BackgroundBox" })
                    if (Math.Abs(N(Field(editor, name), "ActualHeight") - 24) > 1) Issue(name + " is not 24 DIP");
                if (P(Field(editor, "RegexHint"), "Visibility")?.ToString() != "Visible") Issue("regex guidance is hidden");
                S(Field(editor, "ModeBox"), "SelectedIndex", 1); await Settle(editor);
                if (P(Field(editor, "RegexBox"), "IsEnabled") is not false) Issue("HEX mode allows regex");
                S(Field(editor, "ModeBox"), "SelectedIndex", 0); await Settle(editor);
            });

            if (theme == "Dark")
            {
                var invalidEditor = New("SerialMonitor.WinUI.Views.LogRuleDialog", rule, new[] { "Orange" }, sequences);
                S(invalidEditor, "Title", "Edit log rule"); S(invalidEditor, "RequestedTheme", theme);
                await RuleDialog(invalidEditor, "rules-invalid-pattern-" + theme + suffix, async () =>
                {
                    S(Field(invalidEditor, "KeywordBox"), "Text", "[");
                    var button = Tree(invalidEditor).First(e => P(e, "Name")?.ToString() == "PrimaryButton");
                    var peer = New("Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer", button);
                    var invoke = Call(peer, "GetPattern", Enum.Parse(FindType("Microsoft.UI.Xaml.Automation.Peers.PatternInterface"), "Invoke"))!;
                    FindType("Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider").GetMethod("Invoke")!.Invoke(invoke, null);
                    await Settle(invalidEditor);
                    if (P(Field(invalidEditor, "ErrorText"), "Visibility")?.ToString() != "Visible" || P(invalidEditor, "Rule") is not null)
                        Issue("invalid regex was not kept open with an error");
                    var error = Field(invalidEditor, "ErrorText");
                    var errorBounds = (Rect)Call(Call(error, "TransformToVisual", button)!, "TransformBounds",
                        new Rect(0, 0, N(error, "ActualWidth"), N(error, "ActualHeight")))!;
                    if (errorBounds.Bottom > 0 || errorBounds.Top < -N(invalidEditor, "ActualHeight"))
                        Issue("invalid regex error is outside the visible dialog content");
                });
            }

            var export = New("SerialMonitor.WinUI.Views.LogRuleTransferDialog", rules, rule);
            S(export, "RequestedTheme", theme);
            await RuleDialog(export, "rules-export-" + theme + suffix, async () =>
            {
                await WaitForRuleValidation(export);
                if (P(Field(export, "TransferText"), "Text") is not string code || !code.StartsWith("SMRULES:1:", StringComparison.Ordinal))
                    Issue("export did not produce sharing text");
                if (P(Field(export, "TransferText"), "IsReadOnly") is not true) Issue("export is editable");
                S(Field(export, "SelectedOnlyBox"), "IsChecked", true);
                await (Task)export.GetType().GetMethod("UpdateExportAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(export, null)!;
                var selectedText = P(Field(export, "TransferText"), "Text")!.ToString()!;
                using var selectedJson = JsonDocument.Parse(Convert.FromBase64String(selectedText["SMRULES:1:".Length..]));
                if (selectedJson.RootElement.GetArrayLength() != 1) Issue("selected-only export includes other rules");
            });

            var import = New("SerialMonitor.WinUI.Views.LogRuleTransferDialog", null, null);
            S(import, "RequestedTheme", theme);
            await RuleDialog(import, "rules-import-" + theme + suffix, async () =>
            {
                if (P(Field(import, "TransferText"), "Text")?.ToString() != "") Issue("import must start empty");
                S(Field(import, "TransferText"), "Text", "SMRULES:9:broken"); await Task.Delay(300); await Settle(import);
                if (P(import, "IsPrimaryButtonEnabled") is not false) Issue("invalid import is actionable");
                S(Field(import, "TransferText"), "Text", text); await WaitForRuleValidation(import);
                var imported = ((IEnumerable)P(import, "ImportedRules")!).Cast<object>().ToArray();
                if (imported.Length != 2 || P(imported[0], "UseRegex") is not true || P(imported[0], "Keyword")?.ToString() != @"^temp=\d+$")
                    Issue("import did not preserve regex settings");
            });
        }
    }

    private static async Task WaitForRuleValidation(object dialog)
    {
        for (var i = 0; i < 120 && P(dialog, "IsPrimaryButtonEnabled") is not true; i++) await Task.Delay(25);
        if (P(dialog, "IsPrimaryButtonEnabled") is not true) Issue("rule sharing validation did not complete");
        await Settle(dialog);
    }

    private static async Task RuleDialog(object dialog, string name, Func<Task> exercise)
    {
        Case = name; var before = Issues.Count;
        S(dialog, "XamlRoot", P(Root, "XamlRoot")); Call(dialog, "ShowAsync");
        try
        {
            await Settle(dialog); await exercise();
            CheckTextAndButtons(dialog);
            var scrolls = await CheckScrolls(dialog);
            var dialogFooter = await CheckDialogFooter(dialog);
            await CaptureDialog(dialog, name);
            var surface = Tree(dialog).First(e => P(e, "Name")?.ToString() == "BackgroundElement");
            Results.Add(new { name, width = N(surface, "ActualWidth"), height = N(surface, "ActualHeight"), dialogFooter, scrolls, issues = Issues.Count - before });
            Save();
        }
        finally { Call(dialog, "Hide"); await Task.Delay(100); }
    }
}
