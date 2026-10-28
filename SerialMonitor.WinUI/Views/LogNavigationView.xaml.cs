using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

/// <summary>Native date/time selection within the log range captured when the dialog opens.</summary>
public sealed partial class LogNavigationView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(LogTimePickerViewModel), typeof(LogNavigationView),
        new PropertyMetadata(null, OnViewModelChanged));

    private readonly Dictionary<ComboBox, int> _wheelRemainders = new();
    private LogTimePickerViewModel? _subscribedModel;
    private bool _calendarUpdateQueued;

    public LogNavigationView()
    {
        InitializeComponent();
        ApplyLabels();
        foreach (var picker in new[] { YearPicker, MonthPicker, DayPicker, HourPicker, MinutePicker, SecondPicker })
        {
            picker.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(Picker_PointerWheelChanged), true);
        }
        Loaded += View_Loaded;
        Unloaded += View_Unloaded;
    }

    public LogTimePickerViewModel? ViewModel
    {
        get => (LogTimePickerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public void FocusInput() => HourPicker.Focus(FocusState.Programmatic);

    private void ApplyLabels()
    {
        var korean = UiText.Get("LogNavigationLanguage", "en") == "ko";
        DateLabel.Text = korean ? "날짜" : "Date";
        TimeLabel.Text = korean ? "시간" : "Time";
        DateFormatHint.Text = korean ? "연 - 월 - 일" : "YYYY - MM - DD";
        TimeFormatHint.Text = korean ? "24시간 · 시 : 분 : 초" : "24-hour · HH : mm : ss";
        RangeLabel.Text = korean ? "이동 가능 범위" : "Available range";
        RangeStartLabel.Text = korean ? "시작" : "From";
        RangeEndLabel.Text = korean ? "끝" : "To";
        var names = korean
            ? new[] { "연도", "월", "일", "시", "분", "초" }
            : new[] { "Year", "Month", "Day", "Hour", "Minute", "Second" };
        var pickers = new[] { YearPicker, MonthPicker, DayPicker, HourPicker, MinutePicker, SecondPicker };
        for (var index = 0; index < pickers.Length; index++)
        {
            AutomationProperties.SetName(pickers[index], names[index]);
            var hint = korean ? $"{names[index]} · 방향키 또는 마우스 휠로 변경" : $"{names[index]} · use arrow keys or scroll to change";
            AutomationProperties.SetHelpText(pickers[index], hint);
            ToolTipService.SetToolTip(pickers[index], hint);
        }
    }

    private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (LogNavigationView)sender;
        view.NavigationRoot.DataContext = args.NewValue;
        view.SubscribeToModel();
        // Resolve the frozen date presentation before the dialog's first measure.
        view.UpdateCalendar();
    }

    private void View_Loaded(object sender, RoutedEventArgs args)
    {
        SubscribeToModel();
        UpdateCalendar();
    }

    private void View_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribedModel is not null) _subscribedModel.PropertyChanged -= Model_PropertyChanged;
        _subscribedModel = null;
        _wheelRemainders.Clear();
    }

    private void SubscribeToModel()
    {
        if (_subscribedModel is not null) _subscribedModel.PropertyChanged -= Model_PropertyChanged;
        _subscribedModel = IsLoaded ? ViewModel : null;
        if (_subscribedModel is not null) _subscribedModel.PropertyChanged += Model_PropertyChanged;
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LogTimePickerViewModel.ShowYear) or
            nameof(LogTimePickerViewModel.ShowMonth) or nameof(LogTimePickerViewModel.ShowDay) or
            nameof(LogTimePickerViewModel.SelectedYear) or nameof(LogTimePickerViewModel.SelectedMonth) or
            nameof(LogTimePickerViewModel.SelectedDay) or nameof(LogTimePickerViewModel.ErrorText) or
            nameof(LogTimePickerViewModel.RangeText) or null)
        {
            QueueCalendarUpdate();
        }
    }

    private void QueueCalendarUpdate()
    {
        if (_calendarUpdateQueued || !IsLoaded) return;
        _calendarUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _calendarUpdateQueued = false;
            if (IsLoaded) UpdateCalendar();
        })) _calendarUpdateQueued = false;
    }

    private void UpdateCalendar()
    {
        var model = ViewModel;
        var hasDateChoice = model is not null && (model.ShowYear || model.ShowMonth || model.ShowDay);
        DateChoices.Visibility = hasDateChoice ? Visibility.Visible : Visibility.Collapsed;
        FixedDateField.Visibility = hasDateChoice ? Visibility.Collapsed : Visibility.Visible;
        FixedDate.Text = model?.SelectedYear is { } year && model.SelectedMonth is { } month && model.SelectedDay is { } day
            ? string.Format(CultureInfo.InvariantCulture, "{0:0000} - {1:00} - {2:00}", year, month, day)
            : "—";
        var hasError = !string.IsNullOrWhiteSpace(model?.ErrorText);
        AvailableRange.Visibility = hasError ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        var range = model?.RangeText ?? string.Empty;
        ToolTipService.SetToolTip(ErrorText, hasError ? $"{model!.ErrorText}\n{range}" : range);
        AutomationProperties.SetHelpText(NavigationRoot, range);
        foreach (var picker in new[] { YearPicker, MonthPicker, DayPicker, HourPicker, MinutePicker, SecondPicker })
        {
            var wheelHint = ToolTipService.GetToolTip(picker) as string;
            AutomationProperties.SetHelpText(picker, $"{wheelHint}. {range}");
        }
    }

    private void Picker_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not ComboBox picker || picker.IsDropDownOpen || !picker.IsEnabled) return;
        // An open popup retains native list scrolling. Closed selectors step
        // one available value per wheel notch and do not scroll the dialog.
        if (args.Handled) return;
        args.Handled = true;
        var delta = args.GetCurrentPoint(picker).Properties.MouseWheelDelta;
        if (delta == 0 || picker.Items.Count == 0) return;
        _wheelRemainders.TryGetValue(picker, out var remainder);
        remainder += delta;
        var steps = remainder / 120;
        _wheelRemainders[picker] = remainder % 120;
        if (steps == 0) return;
        picker.Focus(FocusState.Pointer);
        var current = Math.Max(0, picker.SelectedIndex);
        picker.SelectedIndex = Math.Clamp(current - steps, 0, picker.Items.Count - 1);
    }
}

/// <summary>Keep native numeric picker values aligned as two-digit clock parts.</summary>
public sealed class LogTimePartConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var digits = parameter is string text && int.TryParse(text, out var parsed) ? parsed : 2;
        return value is int number ? number.ToString(digits == 4 ? "0000" : "00", CultureInfo.InvariantCulture) : "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        DependencyProperty.UnsetValue;
}
