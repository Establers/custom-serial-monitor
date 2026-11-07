using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using NativeToolTipService = Microsoft.UI.Xaml.Controls.ToolTipService;

namespace SerialMonitor.WinUI.Infrastructure;

/// <summary>Keeps native tooltips and bindings, with a shorter mouse hover delay.</summary>
public static class AppToolTipService
{
    // WinUI uses twice SPI_GETMOUSEHOVERTIME (normally 800 ms) and exposes no
    // InitialShowDelay property. Open the same native tooltip after 300 ms.
    internal static readonly TimeSpan MouseHoverDelay = TimeSpan.FromMilliseconds(300);

    public static readonly DependencyProperty ToolTipProperty = DependencyProperty.RegisterAttached(
        "ToolTip", typeof(object), typeof(AppToolTipService), new PropertyMetadata(null, OnToolTipChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(object), typeof(AppToolTipService), new PropertyMetadata(null));

    public static object? GetToolTip(DependencyObject element) => element.GetValue(ToolTipProperty);

    public static void SetToolTip(DependencyObject element, object? value) => element.SetValue(ToolTipProperty, value);

    private static void OnToolTipChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement owner) return;

        var isRemoved = args.NewValue is null || args.NewValue is string { Length: 0 };
        if (owner.GetValue(StateProperty) is HoverState previous)
        {
            // Live bindings (for example HEX BUSY status) may change while the
            // pointer stays over the owner. Keep the same native tip and its
            // pending/open timer so updates neither dismiss nor postpone it.
            if (!isRemoved && args.OldValue is not ToolTip && args.NewValue is not ToolTip)
            {
                previous.UpdateContent(args.NewValue!);
                return;
            }

            previous.Dispose();
            owner.ClearValue(StateProperty);
        }

        if (isRemoved)
        {
            owner.ClearValue(NativeToolTipService.ToolTipProperty);
            return;
        }

        // Keep the source attached property intact, including its binding and
        // x:Uid localization. Native ToolTipService gets a separate explicit tip.
        var tip = args.NewValue as ToolTip ?? new ToolTip { Content = args.NewValue };
        NativeToolTipService.SetToolTip(owner, tip);
        owner.SetValue(StateProperty, new HoverState(owner, tip));
    }

    private sealed class HoverState : IDisposable
    {
        [ThreadStatic]
        private static WeakReference<HoverState>? _activeHover;

        private readonly FrameworkElement _owner;
        private readonly ToolTip _tip;
        private readonly PointerEventHandler _pressedHandler;
        private DispatcherQueueTimer? _timer;
        private bool _hovering;
        private bool _openedByMouse;

        public HoverState(FrameworkElement owner, ToolTip tip)
        {
            _owner = owner;
            _tip = tip;
            _pressedHandler = OnPointerPressed;
            owner.PointerEntered += OnPointerEntered;
            owner.PointerExited += OnPointerExited;
            owner.PointerCanceled += OnPointerExited;
            owner.PointerCaptureLost += OnPointerExited;
            owner.AddHandler(UIElement.PointerPressedEvent, _pressedHandler, true);
            owner.Unloaded += OnUnloaded;
            tip.Closed += OnClosed;
        }

        public void UpdateContent(object content) => _tip.Content = content;

        private void OnPointerEntered(object sender, PointerRoutedEventArgs args)
        {
            if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
                !_owner.IsLoaded || _tip.IsOpen ||
                _owner is Control { IsEnabled: false }) return;

            var point = args.GetCurrentPoint(_owner);
            if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed ||
                point.Properties.IsMiddleButtonPressed) return;

            // A child with its own tooltip takes precedence over its container.
            for (var child = args.OriginalSource as DependencyObject;
                 child is not null && child != _owner;
                 child = VisualTreeHelper.GetParent(child))
            {
                if (NativeToolTipService.GetToolTip(child) is not null) return;
            }

            BeginHover();
        }

        private void BeginHover()
        {
            if (_activeHover?.TryGetTarget(out var previous) == true && previous != this)
                previous.Close();
            _activeHover = new WeakReference<HoverState>(this);
            _hovering = true;
            StartTimer(MouseHoverDelay);
        }

        private void StartTimer(TimeSpan interval)
        {
            StopTimer();
            _timer = _owner.DispatcherQueue.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Interval = interval;
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        private void OnTimerTick(DispatcherQueueTimer sender, object args)
        {
            StopTimer();
            if (_openedByMouse)
            {
                Close();
                return;
            }

            if (!_hovering || !_owner.IsLoaded || _owner.XamlRoot is null ||
                !_owner.XamlRoot.IsHostVisible || _owner.Visibility != Visibility.Visible ||
                _owner is Control { IsEnabled: false } || _tip.IsOpen) return;

            try
            {
                _openedByMouse = true;
                _tip.IsOpen = true;
                // Respect the user's Windows accessibility message duration.
                var duration = new UISettings().MessageDuration;
                StartTimer(TimeSpan.FromSeconds(Math.Max(5, duration)));
            }
            catch (Exception exception)
            {
                Close();
                RuntimeDiagnostics.RecordError("AppToolTipService.Open", exception);
            }
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs args) => Close();

        private void OnPointerPressed(object sender, PointerRoutedEventArgs args) => Close();

        private void OnUnloaded(object sender, RoutedEventArgs args) => Close();

        private void OnClosed(object? sender, object args)
        {
            _openedByMouse = false;
            StopTimer();
        }

        private void Close()
        {
            _hovering = false;
            if (_activeHover?.TryGetTarget(out var active) == true && active == this)
                _activeHover = null;
            StopTimer();
            if (!_openedByMouse) return;
            _openedByMouse = false;
            _tip.IsOpen = false;
        }

        private void StopTimer()
        {
            if (_timer is null) return;
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        public void Dispose()
        {
            Close();
            _owner.PointerEntered -= OnPointerEntered;
            _owner.PointerExited -= OnPointerExited;
            _owner.PointerCanceled -= OnPointerExited;
            _owner.PointerCaptureLost -= OnPointerExited;
            _owner.RemoveHandler(UIElement.PointerPressedEvent, _pressedHandler);
            _owner.Unloaded -= OnUnloaded;
            _tip.Closed -= OnClosed;
        }
    }
}
