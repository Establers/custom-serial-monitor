using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Views;

public sealed partial class LogRuleDialog : ContentDialog
{
    public LogRuleDialog(LogRule source, IEnumerable<string> colors, IEnumerable<CommandSequence> sequences)
    {
        InitializeComponent();
        NameBox.Text = source.Name;
        KeywordBox.Text = source.Keyword;
        EnabledBox.IsChecked = source.Enabled;
        CaseBox.IsChecked = source.CaseSensitive;
        EventBox.IsChecked = source.UseForEvent;
        HighlightBox.IsChecked = source.UseForHighlight;
        FilterBox.IsChecked = source.UseAsViewFilter;
        ModeBox.SelectedIndex = source.Mode == LogRuleMatchMode.Hex ? 1 : 0;
        DirectionBox.ItemsSource = Enum.GetValues<HighlightMatchDirection>();
        DirectionBox.SelectedItem = source.MatchDirection;
        SetOptions(ForegroundBox, colors, source.ForegroundColor);
        SetOptions(BackgroundBox,
            ["(none)", "Default", "Red", "Yellow", "Magenta", "Cyan", "Green", "Blue", "White", "Gray"],
            source.BackgroundColor);
        PriorityBox.Text = source.Priority.ToString(CultureInfo.InvariantCulture);
        CooldownBox.Text = RuleNotificationLimits.ClampCooldown(source.NotificationCooldownSeconds)
            .ToString(CultureInfo.InvariantCulture);
        TrayBox.IsChecked = source.TrayNotificationEnabled;
        SoundBox.IsChecked = source.SoundNotificationEnabled;
        PopupBox.IsChecked = source.PopupNotificationEnabled;
        SetOptions(TriggerSequenceBox,
            new[] { "(none)" }.Concat(sequences.Where(sequence =>
                CommandSequenceTriggerPolicy.CanUseAsTrigger(sequence) ||
                (sequence.Steps.Count > 0 && string.Equals(sequence.Name, source.TriggerSequenceName, StringComparison.OrdinalIgnoreCase)))
                .Select(sequence => sequence.Name)),
            source.TriggerSequenceName);

        DirectionBox.SelectionChanged += (_, _) => UpdateTriggerAvailability();
        ModeBox.SelectionChanged += (_, _) => UpdateCaseAvailability();
        UpdateTriggerAvailability();
        UpdateCaseAvailability();
        SetToolTip(EventBox, "RuleEventToolTip", "Event: add matching lines to Events.");
        SetToolTip(HighlightBox, "RuleHighlightToolTip", "Highlight: color matching lines in the log view.");
        SetToolTip(FilterBox, "RuleFilterToolTip", "Filter: make this rule available in the Filter dropdown.");
        SetToolTip(ModeBox, "RuleModeToolTip",
            "The rule runs only in the selected app mode. Terminal matches decoded text; HEX matches raw bytes.");
        SetToolTip(CaseBox, "RuleCaseToolTip", "Case-sensitive Terminal matching. Ignored when Mode is HEX.");
        SetToolTip(TrayBox, "RuleTrayNotificationToolTip",
            "Windows tray balloon. OFF by default. Events are grouped per rule.");
        SetToolTip(SoundBox, "RuleSoundNotificationToolTip",
            "Play one Windows alert sound per grouped notification. OFF by default.");
        SetToolTip(PopupBox, "RulePopupNotificationToolTip",
            "Show a non-blocking in-app popup for 8 seconds. OFF by default.");
        SetToolTip(CooldownBox, "RuleNotificationCooldownToolTip",
            "Seconds between notifications for this rule (5-86400). Default: 30 seconds.");
        SetToolTip(TriggerSequenceBox, "RuleTriggerSequenceToolTip",
            "Create a sequence with steps on the Sequences tab, then select it here. Matching RX data runs it automatically, even when Event is unchecked. (none) disables it. Unavailable for TX-only rules; additional triggers are ignored while any sequence is running.");
    }

    public LogRule? Rule { get; private set; }

    private HighlightMatchDirection SelectedDirection =>
        DirectionBox.SelectedItem is HighlightMatchDirection direction ? direction : HighlightMatchDirection.Both;

    private void UpdateTriggerAvailability()
    {
        TriggerSequenceBox.IsEnabled = CommandSequenceTriggerPolicy.SupportsRxTrigger(SelectedDirection);
        if (!TriggerSequenceBox.IsEnabled)
        {
            TriggerSequenceBox.SelectedIndex = 0;
            TriggerSequenceHint.Text = UiText.Get("RuleTriggerSequenceTxOnlyHint",
                "Unavailable for TX-only rules. Choose RxOnly or Both to enable RX triggers.");
        }
        else if (TriggerSequenceBox.Items.Count <= 1)
        {
            TriggerSequenceHint.Text = UiText.Get("RuleTriggerSequenceEmptyHint",
                "Create a sequence with steps on the Sequences tab, then select it here to run when RX matches.");
        }
        else
        {
            TriggerSequenceHint.Text = UiText.Get("RuleTriggerSequenceHint",
                "Run the selected command sequence when RX matches this rule. (none) disables it.");
        }
    }

    private void UpdateCaseAvailability() => CaseBox.IsEnabled = ModeBox.SelectedIndex != 1;

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(KeywordBox.Text))
        {
            ShowError(args, KeywordBox, UiText.Get("ValidationKeywordRequired", "Keyword is required."));
            return;
        }

        if (!int.TryParse(PriorityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
        {
            ShowError(args, PriorityBox, UiText.Get("ValidationRulePriorityRange",
                "Priority must be a whole number between -2147483648 and 2147483647."));
            return;
        }

        if (!int.TryParse(CooldownBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown) ||
            cooldown < RuleNotificationLimits.MinCooldownSeconds || cooldown > RuleNotificationLimits.MaxCooldownSeconds)
        {
            ShowError(args, CooldownBox, UiText.Get("ValidationNotificationCooldownRange",
                "Notify cooldown must be between 5 and 86400 seconds (24 hours)."));
            return;
        }

        var background = BackgroundBox.SelectedItem as string;
        var sequence = TriggerSequenceBox.SelectedItem as string;
        Rule = new LogRule
        {
            Name = NameBox.Text,
            Keyword = KeywordBox.Text,
            Enabled = EnabledBox.IsChecked == true,
            CaseSensitive = CaseBox.IsChecked == true,
            UseForEvent = EventBox.IsChecked == true,
            UseForHighlight = HighlightBox.IsChecked == true,
            UseAsViewFilter = FilterBox.IsChecked == true,
            Mode = ModeBox.SelectedIndex == 1 ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal,
            MatchDirection = SelectedDirection,
            ForegroundColor = ForegroundBox.SelectedItem as string ?? "Default",
            BackgroundColor = background == "(none)" ? null : background,
            Priority = priority,
            NotificationCooldownSeconds = cooldown,
            TrayNotificationEnabled = TrayBox.IsChecked == true,
            SoundNotificationEnabled = SoundBox.IsChecked == true,
            PopupNotificationEnabled = PopupBox.IsChecked == true,
            TriggerSequenceName = TriggerSequenceBox.IsEnabled && sequence != "(none)" ? sequence : null
        };
    }

    private void ShowError(ContentDialogButtonClickEventArgs args, Control input, string message)
    {
        args.Cancel = true;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        input.Focus(FocusState.Programmatic);
    }

    private static void SetOptions(ComboBox box, IEnumerable<string> options, string? selected)
    {
        var values = options.ToArray();
        box.ItemsSource = values;
        box.SelectedItem = values.FirstOrDefault(v => string.Equals(v, selected, StringComparison.OrdinalIgnoreCase))
            ?? values.FirstOrDefault();
    }

    private static void SetToolTip(DependencyObject control, string key, string fallback) =>
        ToolTipService.SetToolTip(control, UiText.Get(key, fallback));
}
