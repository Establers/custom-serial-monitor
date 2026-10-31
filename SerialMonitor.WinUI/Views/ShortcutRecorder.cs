using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SerialMonitor.WinUI.Infrastructure;
using Windows.System;

namespace SerialMonitor.WinUI.Views;

internal sealed class ShortcutRecorder : UserControl
{
    private readonly Button _recordButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _clearButton = new();
    private readonly TextBlock _hint = new()
    {
        FontSize = 11,
        Opacity = 0.82,
        TextWrapping = TextWrapping.WrapWholeWords
    };
    private readonly HashSet<VirtualKey> _consumedKeys = new();
    private readonly Func<string, string?> _validateShortcut;
    private bool _isRecording;
    private string? _shortcut;

    public ShortcutRecorder(string? shortcut, Func<string, string?> validateShortcut)
    {
        _shortcut = shortcut;
        _validateShortcut = validateShortcut;
        _recordButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        _recordButton.Click += (_, _) =>
        {
            _isRecording = true;
            _hint.Text = HintText;
            RefreshButtons();
            _recordButton.Focus(FocusState.Programmatic);
        };
        _recordButton.LostFocus += (_, _) => ResetRecording();
        _clearButton.Content = UiText.Get("ShortcutCaptureClear", "Clear");
        AutomationProperties.SetName(_clearButton, UiText.Get("ShortcutCaptureClearName", "Clear shortcut"));
        _clearButton.Click += (_, _) =>
        {
            _shortcut = null;
            CancelRecording();
        };

        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.Children.Add(_recordButton);
        Grid.SetColumn(_clearButton, 1);
        buttons.Children.Add(_clearButton);
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(buttons);
        panel.Children.Add(_hint);
        Content = panel;

        // Preview input is handled before the dialog's Escape/Enter actions and
        // application shortcuts, so recording cannot save, close, or send TX.
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Unloaded += (_, _) => ResetRecording();
        CancelRecording();
    }

    public string? Shortcut => _shortcut;

    private static string HintText => UiText.Get("ShortcutCaptureHint",
        "Press an unused key (e.g. F1). Existing app shortcuts are reserved. Esc cancels.");

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        // Keep repeated key-downs consumed until key-up, even after committing.
        if (_consumedKeys.Contains(args.Key))
        {
            args.Handled = true;
            return;
        }
        if (!_isRecording)
        {
            return;
        }

        args.Handled = true;
        _consumedKeys.Add(args.Key);
        if (AppShortcuts.Matches(AppShortcutAction.EditorCancel, args.Key))
        {
            CancelRecording();
            return;
        }

        if (SavedCommandShortcut.IsModifierKey(args.Key))
        {
            return;
        }

        var gesture = SavedCommandShortcut.CreateGestureText(args.Key);
        if (ShortcutReservations.Find(gesture) is { } reservation)
        {
            _hint.Text = reservation.FormatUiError(gesture!);
            return;
        }

        var shortcut = SavedCommandShortcut.CreateText(args.Key);
        if (shortcut is null)
        {
            _hint.Text = UiText.Get("ShortcutCaptureUnsupported",
                "Use an unused single key, Ctrl+digit or Alt+letter/digit. Existing app shortcuts are reserved. Esc cancels.");
            return;
        }

        if (_validateShortcut(shortcut) is { } conflict)
        {
            _hint.Text = conflict;
            return;
        }

        _shortcut = shortcut;
        CancelRecording();
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        // In particular, Space/Enter must not click the record button on release.
        if (_consumedKeys.Remove(args.Key) || _isRecording)
        {
            args.Handled = true;
        }
    }

    private void CancelRecording()
    {
        _isRecording = false;
        _hint.Text = string.IsNullOrWhiteSpace(_shortcut) ? HintText
            : ShortcutReservations.Find(_shortcut) is { } reservation ? reservation.FormatUiError(_shortcut)
            : _validateShortcut(_shortcut) ?? HintText;
        RefreshButtons();
    }

    private void ResetRecording()
    {
        // Focus can leave the window before the corresponding key-up arrives.
        _consumedKeys.Clear();
        CancelRecording();
    }

    private void RefreshButtons()
    {
        _recordButton.Content = _isRecording
            ? UiText.Get("ShortcutCaptureListening", "Press a shortcut… (Esc to cancel)")
            : string.IsNullOrWhiteSpace(_shortcut)
                ? UiText.Get("ShortcutCaptureIdle", "Click to set shortcut")
                : _shortcut;
        AutomationProperties.SetName(_recordButton, (string)_recordButton.Content);
        AutomationProperties.SetHelpText(_recordButton, HintText);
        _clearButton.IsEnabled = !string.IsNullOrWhiteSpace(_shortcut);
    }
}
