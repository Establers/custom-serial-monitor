using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Views;

internal sealed class SequenceStepDialog : ContentDialog
{
    public CommandSequenceStep? Result { get; private set; }

    public SequenceStepDialog(CommandSequenceStep source, TxSendMode inheritedMode, TxSendMode? defaultTxMode = null)
    {
        var edit = new SequenceStepEditSession(source);
        source = edit.Draft;
        Title = "시퀀스 단계";
        // Use the same dialog width for both kinds, independent of hint length.
        Resources["ContentDialogMinWidth"] = 440d;
        Resources["ContentDialogMaxWidth"] = 440d;
        PrimaryButtonText = "저장"; CloseButtonText = "취소";
        DefaultButton = ContentDialogButton.Primary;
        var kind = new ComboBox { Header = "유형", ItemsSource = new[] { "TX", "RX Check" },
            SelectedIndex = source.Kind == CommandSequenceStepKind.RxCheck ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var command = new TextBox { Header = "내용", FontFamily = new FontFamily("Consolas"),
            AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap,
            Text = SingleLineCommandText.ForEditing(source.CommandText) };
        var lineBreakHint = new TextBlock { Text = "기존 내용의 줄바꿈을 공백으로 표시했습니다. 내용을 확인한 뒤 저장하세요.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75,
            Visibility = SingleLineCommandText.HasLineBreaks(source.CommandText) ? Visibility.Visible : Visibility.Collapsed };
        var mode = new ComboBox { Header = "TX 모드", ItemsSource = new[] { "Global", "Terminal", "Hex" },
            SelectedIndex = source.TxMode switch { TxSendMode.Terminal => 1, TxSendMode.Hex => 2, _ => 0 },
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var ending = new ComboBox { Header = "줄 끝", ItemsSource = new[] { "Global", "None", "CR", "LF", "CRLF" },
            SelectedIndex = source.LineEndingMode switch { TxLineEndingMode.None => 1, TxLineEndingMode.Cr => 2,
                TxLineEndingMode.Lf => 3, TxLineEndingMode.Crlf => 4, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var txSettings = new Grid { ColumnSpacing = 12 };
        txSettings.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        txSettings.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        txSettings.Children.Add(mode); Grid.SetColumn(ending, 1); txSettings.Children.Add(ending);
        var duration = new TextBox { Header = "대기 시간 (ms)", Text = source.DelayAfterMs.ToString(CultureInfo.InvariantCulture),
            PlaceholderText = "0 ~ 600000", HorizontalAlignment = HorizontalAlignment.Stretch };
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .75, FontSize = 12 };
        var format = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var followTxMode = new HyperlinkButton { Content = "TX 모드 따르기", Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        var name = new TextBox { Header = "이름 (선택)", Text = source.Name ?? "" };
        var comment = new TextBox { Header = "메모 (선택)", Text = source.Comment ?? "" };
        var metadata = new StackPanel { Spacing = 10 }; metadata.Children.Add(name); metadata.Children.Add(comment);
        var error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 14, MinWidth = 340 };
        foreach (var item in new UIElement[] { kind, command, lineBreakHint, format, followTxMode, txSettings, duration, hint, metadata, error }) panel.Children.Add(item);
        Content = new ScrollViewer { Content = panel, MaxHeight = 590, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        TxSendMode? SelectedMode() => mode.SelectedIndex switch { 1 => TxSendMode.Terminal, 2 => TxSendMode.Hex, _ => null };
        void Update()
        {
            var rx = kind.SelectedIndex == 1;
            var effectiveMode = rx ? source.TxMode ?? inheritedMode : SelectedMode() ?? defaultTxMode ?? inheritedMode;
            command.Header = rx ? "기대 RX 패턴" : "전송할 내용";
            command.PlaceholderText = effectiveMode == TxSendMode.Hex ? (rx ? "AA 55 ?? 00" : "AA 10 01") : (rx ? "READY" : "status");
            txSettings.Visibility = rx ? Visibility.Collapsed : Visibility.Visible;
            ending.IsEnabled = effectiveMode != TxSendMode.Hex;
            var modeName = effectiveMode == TxSendMode.Hex ? "HEX" : "Terminal";
            format.Text = rx ? source.TxMode.HasValue ? $"{modeName} 모드 · 이 단계에 저장된 모드입니다."
                : $"{modeName} 모드 · 시퀀스의 TX 모드를 따릅니다." : "Global은 실행 시작 시 메인 TX 설정을 사용합니다.";
            followTxMode.Visibility = rx && source.TxMode.HasValue ? Visibility.Visible : Visibility.Collapsed;
            duration.Header = rx ? "RX 확인 시간 (ms)" : "전송 후 대기 (ms)";
            hint.Text = rx ? "설정한 시간 전체를 기다립니다. 일치해도 대기는 유지됩니다. 직전 TX 이후 도착한 RX도 확인합니다."
                + (effectiveMode == TxSendMode.Hex ? "\n??는 한 바이트를 무시합니다." : "\n입력한 문자를 그대로 찾습니다. 대소문자를 구분합니다.")
                : "전송한 뒤 설정한 시간만큼 기다리고 다음 단계로 진행합니다.";
        }
        kind.SelectionChanged += (_, _) => Update(); mode.SelectionChanged += (_, _) => Update(); Update();
        followTxMode.Click += (_, _) => { edit.FollowTxMode(); Update(); };
        command.BeforeTextChanging += (sender, args) =>
        {
            if (!SingleLineCommandText.HasLineBreaks(args.NewText)) return;
            args.Cancel = true;
            error.Text = "TX 내용과 RX 패턴은 한 줄만 입력할 수 있습니다.";
        };
        PrimaryButtonClick += (dialog, args) =>
        {
            try
            {
                var rx = kind.SelectedIndex == 1;
                var selectedMode = rx ? source.TxMode : SelectedMode();
                if (SingleLineCommandText.HasLineBreaks(command.Text))
                    throw new ArgumentException("TX 내용과 RX 패턴은 한 줄만 입력할 수 있습니다.");
                if (command.Text.Length == 0 || (!rx && selectedMode is null && string.IsNullOrWhiteSpace(command.Text)))
                    throw new ArgumentException(rx ? "기대 RX 패턴을 입력하세요." : "전송할 내용을 입력하세요.");
                if (!int.TryParse(duration.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) || ms is < 0 or > 600000)
                    throw new ArgumentException("시간은 0 ~ 600000 ms로 입력하세요.");
                if (rx) new ResponseCheck((selectedMode ?? inheritedMode) == TxSendMode.Hex ? "hex" : "text", command.Text).Validate();
                else if ((selectedMode ?? defaultTxMode ?? inheritedMode) == TxSendMode.Hex && !HexPayloadParser.TryParse(command.Text, out _, out var message))
                    throw new ArgumentException(message);
                Result = edit.CreateResult();
                Result.Kind = rx ? CommandSequenceStepKind.RxCheck : CommandSequenceStepKind.Tx;
                Result.CommandText = command.Text; Result.TxMode = selectedMode;
                Result.DelayAfterMs = ms; Result.Name = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
                Result.Comment = string.IsNullOrWhiteSpace(comment.Text) ? null : comment.Text.Trim();
                Result.LineEndingMode = rx ? TxLineEndingMode.None : ending.SelectedIndex switch
                { 1 => TxLineEndingMode.None, 2 => TxLineEndingMode.Cr, 3 => TxLineEndingMode.Lf, 4 => TxLineEndingMode.Crlf, _ => null };
            }
            catch (ArgumentException ex) { args.Cancel = true; error.Text = ex.Message; }
        };
    }
}
