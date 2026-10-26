using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace SerialMonitor.WinUI.Infrastructure;

// TextBox deliberately has no numeric wheel/arrow-key stepping or spin buttons.
public sealed class PercentageTextBox : TextBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(PercentageTextBox), new PropertyMetadata(0, OnValueChanged));

    private bool _updatingText;

    public PercentageTextBox()
    {
        Text = "0";
        BeforeTextChanging += OnBeforeTextChanging;
        TextChanging += OnTextChanging;
        LostFocus += OnLostFocus;
        KeyDown += OnKeyDown;
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0, 100));
    }

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (PercentageTextBox)sender;
        var value = (int)args.NewValue;
        var clamped = Math.Clamp(value, 0, 100);
        if (value != clamped)
        {
            editor.Value = clamped;
            return;
        }

        editor.RestoreValueText();
    }

    private void OnBeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        // Allow clearing while editing, but reject an invalid edit/paste as a whole.
        args.Cancel = args.NewText.Length > 0 && !PercentageInput.TryParse(args.NewText, out _);
    }

    private void OnTextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        if (_updatingText || !PercentageInput.TryParse(Text, out var value))
        {
            return;
        }

        Value = value;
        // Normalize even when Value did not change (e.g. 100 -> 1000 or 25 -> 025).
        RestoreValueText();
    }

    private void OnLostFocus(object sender, RoutedEventArgs args) => RestoreValueText();

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            RestoreValueText();
            args.Handled = true;
        }
    }

    private void RestoreValueText()
    {
        var normalized = Value.ToString(CultureInfo.InvariantCulture);
        if (Text == normalized)
        {
            return;
        }

        _updatingText = true;
        try
        {
            Text = normalized;
            Select(normalized.Length, 0);
        }
        finally
        {
            _updatingText = false;
        }
    }
}
