using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Views;

/// <summary>Numeric type-ahead for the bounded date/time choices in log navigation.</summary>
public sealed class NumericTimePartPicker : ComboBox
{
    public static readonly DependencyProperty CommittedValueProperty = DependencyProperty.Register(
        nameof(CommittedValue), typeof(object), typeof(NumericTimePartPicker),
        new PropertyMetadata(null, OnCommittedValueChanged));

    private static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(1);
    private UIElement? _popupInputRoot;
    private string _digits = string.Empty;
    private long _lastInput;
    private bool _isBrowsing;
    private bool _updatingSelection;
    private bool _focusAfterClose;

    public NumericTimePartPicker()
    {
        // Native text search uses item text and treats repeated digits as list
        // cycling. Here 4,4 must mean 44, including for formatted integer items.
        IsTextSearchEnabled = false;
        PreviewKeyDown += HandlePreviewKeyDown;
        DropDownOpened += (_, _) => { _isBrowsing = true; _focusAfterClose = false; ResetInput(); AttachPopupInput(); };
        DropDownClosed += (_, _) =>
        {
            _isBrowsing = false;
            ResetInput();
            CommitSelection();
            var advance = _focusAfterClose;
            _focusAfterClose = false;
            // Native close animation returns focus to the picker. Advance only
            // after it finishes, so that it cannot reclaim focus from the button.
            if (advance && IsLoaded) ConfirmationFocusTarget?.Focus(FocusState.Keyboard);
        };
        SelectionChanged += (_, _) => CommitSelection();
        RegisterPropertyChangedCallback(ItemsSourceProperty, (_, _) => ApplyCommittedValue());
        LostFocus += (_, _) => { if (!IsDropDownOpen) ResetInput(); };
        Loaded += (_, _) => AttachPopupInput();
        Unloaded += (_, _) => { _isBrowsing = false; _focusAfterClose = false; ResetInput(); DetachPopupInput(); };
        AddHandler(PointerPressedEvent, new PointerEventHandler(ResetPointerInput), true);
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(ResetPointerInput), true);
    }

    public int DigitCount { get; set; } = 2;
    internal Control? ConfirmationFocusTarget { get; set; }

    // Bind the model to the committed value, not the tentative popup selection.
    // Updating the model during type-ahead rebuilds the calendar lists, removes
    // the focused item and dismisses the popup after the first digit.
    public object? CommittedValue
    {
        get => GetValue(CommittedValueProperty);
        set => SetValue(CommittedValueProperty, value);
    }

    private static void OnCommittedValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((NumericTimePartPicker)sender).ApplyCommittedValue();

    private void ApplyCommittedValue()
    {
        _updatingSelection = true;
        try { SelectedItem = CommittedValue; }
        finally { _updatingSelection = false; }
    }

    private void CommitSelection()
    {
        // Native Enter/click commits and Escape/outside-click reverts before
        // DropDownClosed. Preserve those semantics throughout the close animation.
        if (!IsLoaded || _updatingSelection || _isBrowsing || SelectedItem is not int value) return;
        CommittedValue = value;
    }

    protected override void OnApplyTemplate()
    {
        DetachPopupInput();
        base.OnApplyTemplate();
        AttachPopupInput();
    }

    private void AttachPopupInput()
    {
        // An open ComboBox hosts keyboard focus in a separate popup tree.
        var root = (GetTemplateChild("Popup") as Popup)?.Child;
        if (ReferenceEquals(root, _popupInputRoot)) return;
        DetachPopupInput();
        _popupInputRoot = root;
        if (_popupInputRoot is null) return;
        _popupInputRoot.PreviewKeyDown += HandlePreviewKeyDown;
        _popupInputRoot.AddHandler(PointerPressedEvent, new PointerEventHandler(ResetPointerInput), true);
        _popupInputRoot.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(ResetPointerInput), true);
    }

    private void DetachPopupInput()
    {
        if (_popupInputRoot is null) return;
        _popupInputRoot.PreviewKeyDown -= HandlePreviewKeyDown;
        _popupInputRoot.RemoveHandler(PointerPressedEvent, new PointerEventHandler(ResetPointerInput));
        _popupInputRoot.RemoveHandler(PointerWheelChangedEvent, new PointerEventHandler(ResetPointerInput));
        _popupInputRoot = null;
    }

    private void HandlePreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || !IsEnabled || Items.Count == 0) return;
        if (ConfirmationFocusTarget is { IsEnabled: true } target &&
            (AppShortcuts.Matches(AppShortcutAction.EditorConfirm, args.Key) ||
             AppShortcuts.Matches(AppShortcutAction.FocusNext, args.Key)))
        {
            args.Handled = true;
            if (args.KeyStatus.WasKeyDown) return;
            ResetInput();
            if (IsDropDownOpen)
            {
                // Arrow-key browsing can focus a different item without changing
                // SelectedItem. Commit that item as native Enter would do.
                if (FocusManager.GetFocusedElement(XamlRoot) is ComboBoxItem focusedItem && IndexFromContainer(focusedItem) >= 0)
                    SelectedItem = ItemFromContainer(focusedItem);
                _focusAfterClose = true;
                IsDropDownOpen = false;
            }
            else
            {
                if (_isBrowsing)
                {
                    _focusAfterClose = true;
                    return;
                }
                CommitSelection();
                target.Focus(FocusState.Keyboard);
            }
            return;
        }
        // Reuse the input adapter, including numpad normalization and modifier
        // handling. Modified keys retain their normal control/shortcut behavior.
        var text = SavedCommandShortcut.CreateGestureText(args.Key);
        if (text is not { Length: 1 } || !char.IsAsciiDigit(text[0]))
        {
            ResetInput();
            return;
        }

        args.Handled = true;
        if (args.KeyStatus.WasKeyDown) return;
        var digitCount = Math.Clamp(DigitCount, 1, 4);
        if (_digits.Length >= digitCount || Stopwatch.GetElapsedTime(_lastInput) > InputTimeout)
            ResetInput();
        _lastInput = Stopwatch.GetTimestamp();

        var input = _digits + text;
        var match = FindMatch(input, digitCount);
        if (match is null && _digits.Length > 0)
        {
            // An impossible continuation starts a fresh number, like native
            // type-ahead. Never synthesize an item outside the available range.
            input = text;
            match = FindMatch(input, digitCount);
        }
        _digits = input;
        if (match is not { } value) return;

        SelectedItem = value;
        if (IsDropDownOpen && ContainerFromItem(value) is ComboBoxItem item)
        {
            item.Focus(FocusState.Keyboard);
            item.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private int? FindMatch(string input, int digitCount)
    {
        var number = int.Parse(input, CultureInfo.InvariantCulture);
        int? prefixMatch = null;
        var format = digitCount == 4 ? "D4" : "D2";
        foreach (var item in Items)
        {
            if (item is not int value) continue;
            if (value == number) return value;
            if (prefixMatch is null && value.ToString(format, CultureInfo.InvariantCulture).StartsWith(input, StringComparison.Ordinal))
                prefixMatch = value;
        }
        return prefixMatch;
    }

    private void ResetPointerInput(object sender, PointerRoutedEventArgs args) => ResetInput();
    private void ResetInput() => _digits = string.Empty;
}
