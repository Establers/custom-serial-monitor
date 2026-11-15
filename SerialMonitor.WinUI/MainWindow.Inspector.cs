using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private bool _isInspectorCollapsed;
    private bool _isInspectorResizeDragging;
    private bool _inspectorResizeMoved;
    private double _inspectorResizeStartX;
    private double _inspectorResizeStartY;
    private double _inspectorResizeStartHeight;
    private double _inspectorResizeStartXtermHeight;
    private double _minimumXtermHeightDuringResize;
    private double _pendingInspectorResizeHeight;
    private double _liveInspectorResizeHeight = double.NaN;
    private double _lastExpandedInspectorHeight = double.NaN;
    private long _lastInspectorClickTimestamp;
    private uint _lastInspectorClickPointerId;
    private double _lastInspectorClickX;
    private double _lastInspectorClickY;
    private uint? _activeInspectorResizePointerId;
    private long _xtermResizeShieldGeneration;

    private const double InspectorCollapseThreshold = 56;
    private const double InspectorDragThreshold = 3;
    private const double InspectorDoubleClickDistance = 8;
    private static readonly TimeSpan XtermResizeShieldDuration = TimeSpan.FromMilliseconds(110);

    private void InspectorResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (IsClosingOrClosed) return;
        var point = args.GetCurrentPoint(MainContentGrid);
        if (_activeDockPointerId is not null || _isInspectorResizeDragging ||
            !point.Properties.IsLeftButtonPressed ||
            sender is not UIElement handle)
        {
            return;
        }

        _inspectorResizeMoved = false;
        _inspectorResizeStartX = point.Position.X;
        _inspectorResizeStartY = point.Position.Y;
        _inspectorResizeStartHeight = _isInspectorCollapsed
            ? 0
            : InspectorExtent;
        _inspectorResizeStartXtermHeight = XtermLogSurface.ActualHeight;
        _minimumXtermHeightDuringResize = _inspectorResizeStartXtermHeight;
        _pendingInspectorResizeHeight = _inspectorResizeStartHeight;
        _liveInspectorResizeHeight = double.NaN;

        if (!_isInspectorCollapsed && _inspectorResizeStartHeight > InspectorCollapseThreshold)
        {
            RememberInspectorExtent(_inspectorResizeStartHeight);
        }

        InspectorPanel.MinHeight = 0;
        if (!handle.CapturePointer(args.Pointer))
        {
            _isInspectorResizeDragging = false;
            _inspectorResizeMoved = false;
            _liveInspectorResizeHeight = double.NaN;
            _activeInspectorResizePointerId = null;
            ResetInspectorClickTracking();
            return;
        }

        Interlocked.Increment(ref _xtermResizeShieldGeneration);
        ResetXtermResizeShieldVisual();
        _activeInspectorResizePointerId = args.Pointer.PointerId;
        _isInspectorResizeDragging = true;
        args.Handled = true;
    }

    private void InspectorResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_isInspectorResizeDragging ||
            _activeInspectorResizePointerId != args.Pointer.PointerId)
        {
            return;
        }

        var point = args.GetCurrentPoint(MainContentGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ResetInspectorClickTracking();
            FinishInspectorResize(sender as UIElement, releasePointerCapture: true);
            return;
        }

        if (InspectorResizeInteractionPolicy.HasMoved(
                _inspectorResizeStartX,
                _inspectorResizeStartY,
                point.Position.X,
                point.Position.Y,
                InspectorDragThreshold))
        {
            _inspectorResizeMoved = true;
        }

        var inspectorHeight = InspectorDockLayout.ResizeExtent(
            _inspectorDockPosition, _inspectorResizeStartHeight,
            point.Position.X - _inspectorResizeStartX, point.Position.Y - _inspectorResizeStartY,
            MaximumInspectorExtent(_inspectorDockPosition));
        _pendingInspectorResizeHeight = inspectorHeight;
        if (_inspectorResizeMoved)
        {
            if (_inspectorDockPosition == InspectorDockPosition.Bottom)
                UpdateXtermResizeShieldDuringDrag(inspectorHeight);
            ApplyInspectorResize(inspectorHeight, persistExpandedHeight: false);
        }
        args.Handled = true;
    }

    private void InspectorResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_isInspectorResizeDragging ||
            _activeInspectorResizePointerId != args.Pointer.PointerId)
        {
            return;
        }

        var point = args.GetCurrentPoint(MainContentGrid);
        var pointerId = args.Pointer.PointerId;
        var wasClick = !_inspectorResizeMoved &&
            Math.Abs(point.Position.X - _inspectorResizeStartX) <= InspectorDragThreshold &&
            Math.Abs(point.Position.Y - _inspectorResizeStartY) <= InspectorDragThreshold;

        FinishInspectorResize(sender as UIElement, releasePointerCapture: true);
        if (wasClick)
        {
            RegisterInspectorHandleClick(pointerId, point.Position.X, point.Position.Y);
        }
        else
        {
            ResetInspectorClickTracking();
        }

        args.Handled = true;
    }

    private void InspectorResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (!_isInspectorResizeDragging ||
            _activeInspectorResizePointerId != args.Pointer.PointerId)
        {
            return;
        }

        ResetInspectorClickTracking();
        FinishInspectorResize(sender as UIElement, releasePointerCapture: false);
    }

    private void RegisterInspectorHandleClick(uint pointerId, double x, double y)
    {
        var clickTimestamp = Stopwatch.GetTimestamp();
        var isDoubleClick = _lastInspectorClickTimestamp != 0 &&
            InspectorResizeInteractionPolicy.IsDoubleClick(
                Stopwatch.GetElapsedTime(_lastInspectorClickTimestamp, clickTimestamp),
                TimeSpan.FromMilliseconds(GetDoubleClickTime()),
                _lastInspectorClickPointerId,
                pointerId,
                _lastInspectorClickX,
                _lastInspectorClickY,
                x,
                y,
                InspectorDoubleClickDistance);

        if (isDoubleClick)
        {
            ResetInspectorClickTracking();
            ToggleInspectorCollapsed();
            return;
        }

        _lastInspectorClickTimestamp = clickTimestamp;
        _lastInspectorClickPointerId = pointerId;
        _lastInspectorClickX = x;
        _lastInspectorClickY = y;
    }

    private void ResetInspectorClickTracking()
    {
        _lastInspectorClickTimestamp = 0;
        _lastInspectorClickPointerId = 0;
        _lastInspectorClickX = 0;
        _lastInspectorClickY = 0;
    }

    private void FinishInspectorResize(UIElement? handle, bool releasePointerCapture)
    {
        if (!_isInspectorResizeDragging)
        {
            return;
        }

        var shouldCommitResize = _inspectorResizeMoved;
        _isInspectorResizeDragging = false;
        _inspectorResizeMoved = false;
        _activeInspectorResizePointerId = null;
        if (releasePointerCapture)
        {
            handle?.ReleasePointerCaptures();
        }

        if (shouldCommitResize)
        {
            CommitInspectorResize(_pendingInspectorResizeHeight);
        }
        else
        {
            _liveInspectorResizeHeight = double.NaN;
            ResetXtermResizeShieldVisual();
            QueueXtermFit();
        }
    }

    private void InterruptInspectorInteraction()
    {
        CancelInspectorDockDrag();
        ResetInspectorClickTracking();
        // Window activation/geometry can change before CaptureLost is delivered.
        // Clear the gesture before releasing capture so reentrant events are inert.
        FinishInspectorResize(InspectorResizeHandle, releasePointerCapture: true);
    }

    private void ApplyInspectorResize(double inspectorHeight, bool persistExpandedHeight)
    {
        _isInspectorCollapsed = inspectorHeight <= InspectorCollapseThreshold;
        _liveInspectorResizeHeight = persistExpandedHeight
            ? double.NaN
            : inspectorHeight;
        if (persistExpandedHeight && !_isInspectorCollapsed)
        {
            RememberInspectorExtent(inspectorHeight);
        }

        ApplyInspectorLayout();
    }

    private void ToggleInspectorCollapsed()
    {
        if (IsClosingOrClosed) return;
        _liveInspectorResizeHeight = double.NaN;
        if (!_isInspectorCollapsed && InspectorExtent > InspectorCollapseThreshold)
        {
            RememberInspectorExtent(InspectorExtent);
        }

        _isInspectorCollapsed = !_isInspectorCollapsed;
        BeginXtermResizeTransition();
        ApplyInspectorLayout();
        QueueXtermFit();
        QueueInspectorLayoutSave();
    }

    private void CommitInspectorResize(double inspectorHeight)
    {
        ApplyInspectorResize(inspectorHeight, persistExpandedHeight: true);
        QueueInspectorLayoutSave();
        QueueXtermFit();
        var generation = Interlocked.Read(ref _xtermResizeShieldGeneration);
        _ = HideXtermResizeShieldAsync(generation);
    }

    private void UpdateXtermResizeShieldDuringDrag(double inspectorHeight)
    {
        var appliedInspectorHeight = inspectorHeight <= InspectorCollapseThreshold
            ? 0
            : inspectorHeight;
        var expectedXtermHeight = Math.Max(
            0,
            _inspectorResizeStartXtermHeight + _inspectorResizeStartHeight - appliedInspectorHeight);
        _minimumXtermHeightDuringResize = Math.Min(
            _minimumXtermHeightDuringResize,
            expectedXtermHeight);
        var newlyExposedHeight = expectedXtermHeight - _minimumXtermHeightDuringResize;
        if (newlyExposedHeight <= 0.5)
        {
            ResetXtermResizeShieldVisual();
            return;
        }

        XtermResizeShield.VerticalAlignment = VerticalAlignment.Bottom;
        XtermResizeShield.Height = Math.Min(expectedXtermHeight, newlyExposedHeight + 2);
        XtermResizeShield.Visibility = Visibility.Visible;
    }

    private void BeginXtermResizeTransition()
    {
        var generation = Interlocked.Increment(ref _xtermResizeShieldGeneration);
        XtermResizeShield.VerticalAlignment = VerticalAlignment.Stretch;
        XtermResizeShield.Height = double.NaN;
        XtermResizeShield.Visibility = Visibility.Visible;
        _ = HideXtermResizeShieldAsync(generation);
    }

    private async Task HideXtermResizeShieldAsync(long generation)
    {
        await Task.Delay(XtermResizeShieldDuration).ConfigureAwait(false);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != Interlocked.Read(ref _xtermResizeShieldGeneration) || IsClosingOrClosed)
            {
                return;
            }

            ResetXtermResizeShieldVisual();
        });
    }

    private void ResetXtermResizeShieldVisual()
    {
        XtermResizeShield.Visibility = Visibility.Collapsed;
        XtermResizeShield.VerticalAlignment = VerticalAlignment.Bottom;
        XtermResizeShield.Height = 0;
    }

}
