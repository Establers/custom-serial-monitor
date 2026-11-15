using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private InspectorDockPosition _inspectorDockPosition = InspectorDockPosition.Bottom;
    private double _lastExpandedInspectorWidth = double.NaN;
    private uint? _activeDockPointerId;
    private bool _dockDragMoved;
    private Windows.Foundation.Point _dockDragStart;

    private double InspectorExtent => _inspectorDockPosition == InspectorDockPosition.Bottom
        ? InspectorPanel.ActualHeight : InspectorPanel.ActualWidth;

    private void RememberInspectorExtent(double extent)
    {
        if (_inspectorDockPosition == InspectorDockPosition.Bottom) _lastExpandedInspectorHeight = extent;
        else _lastExpandedInspectorWidth = extent;
    }

    private double MaximumInspectorExtent(InspectorDockPosition position) => InspectorDockLayout.MaximumExtent(
        position, MainContentGrid.ActualWidth, MainContentGrid.ActualHeight, ContentCommandsRow.ActualHeight);

    private double ExpandedInspectorExtent(InspectorDockPosition position)
    {
        var bottom = position == InspectorDockPosition.Bottom;
        var remembered = bottom ? _lastExpandedInspectorHeight : _lastExpandedInspectorWidth;
        var desired = double.IsFinite(remembered) ? remembered : bottom
            ? (MainContentGrid.ActualHeight - ContentCommandsRow.ActualHeight - InspectorDockLayout.SplitterSize) / 3
            : Math.Max(InspectorDockLayout.MinimumSideWidth, MainContentGrid.ActualWidth * 0.4);
        return Math.Clamp(desired, 0, MaximumInspectorExtent(position));
    }

    private void InspectorWorkspace_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        CancelInspectorDockDrag();
        ApplyInspectorLayout();
    }

    private void ApplyInspectorLayout()
    {
        var bottom = _inspectorDockPosition == InspectorDockPosition.Bottom;
        var left = _inspectorDockPosition == InspectorDockPosition.Left;
        var extent = _isInspectorCollapsed ? 0 :
            _isInspectorResizeDragging && double.IsFinite(_liveInspectorResizeHeight)
                ? Math.Clamp(_liveInspectorResizeHeight, 0, MaximumInspectorExtent(_inspectorDockPosition))
                : ExpandedInspectorExtent(_inspectorDockPosition);
        var mainColumn = left ? 2 : 0;

        // Only move existing grid children. In particular, do not recreate the
        // WebView, tabs, view-models or the Protocol view's refresh subscription.
        InspectorPanel.Visibility = _isInspectorCollapsed ? Visibility.Collapsed : Visibility.Visible;
        InspectorTabView.Visibility = InspectorPanel.Visibility;
        InspectorPanel.MinHeight = 0;
        ContentLogRow.Height = new GridLength(1, GridUnitType.Star);
        ContentResizeHandleRow.Height = new GridLength(bottom ? InspectorDockLayout.SplitterSize : 0);
        ContentInspectorRow.Height = new GridLength(bottom ? extent : 0);
        LogColumn.Width = left ? new GridLength(extent) : new GridLength(1, GridUnitType.Star);
        InspectorSplitterColumn.Width = new GridLength(bottom ? 0 : InspectorDockLayout.SplitterSize);
        InspectorColumn.Width = left ? new GridLength(1, GridUnitType.Star) : new GridLength(bottom ? 0 : extent);
        Grid.SetColumn(LogPanel, mainColumn);
        Grid.SetColumn(CommandsPanel, mainColumn);
        Grid.SetColumn(InspectorPanel, bottom ? 0 : left ? 0 : 2);
        Grid.SetRow(InspectorPanel, bottom ? 3 : 0);
        Grid.SetRowSpan(InspectorPanel, bottom ? 1 : 4);

        foreach (var element in new FrameworkElement[] { InspectorResizeTrack, InspectorResizeHandle })
        {
            Grid.SetRow(element, bottom ? 1 : 0);
            Grid.SetRowSpan(element, bottom ? 1 : 4);
            Grid.SetColumn(element, bottom ? 0 : 1);
        }
        InspectorResizeHandle.SetHorizontal(!bottom);
        InspectorResizeHandle.Width = bottom ? double.NaN : 20;
        InspectorResizeHandle.Height = bottom ? 20 : double.NaN;
        InspectorResizeHandle.HorizontalAlignment = bottom ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        InspectorResizeHandle.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        InspectorResizeTrackBorder.BorderThickness = bottom ? new Thickness(0, 1, 0, 1) : new Thickness(1, 0, 1, 0);
        InspectorResizeTrackGrip.Width = bottom ? 36 : 2;
        InspectorResizeTrackGrip.Height = bottom ? 2 : 36;
        AppToolTipService.SetToolTip(InspectorResizeHandle, UiText.Get(
            _isInspectorCollapsed ? "InspectorDockRestoreToolTip" : "InspectorDockResizeToolTip",
            _isInspectorCollapsed ? "Drag toward the log or double-click to restore the panel."
                : "Drag to resize the panel. Double-click to collapse it."));
    }

    private void DockInspector(InspectorDockPosition position)
    {
        CancelInspectorDockDrag();
        if (_isInspectorResizeDragging) return;
        if (!_isInspectorCollapsed && InspectorExtent > InspectorCollapseThreshold)
            RememberInspectorExtent(InspectorExtent);
        _inspectorDockPosition = position;
        _isInspectorCollapsed = false;
        _liveInspectorResizeHeight = double.NaN;
        ResetInspectorClickTracking();
        BeginXtermResizeTransition();
        ApplyInspectorLayout();
        QueueXtermFit();
        QueueInspectorLayoutSave();
    }

    private void InspectorDockHandle_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(MainContentGrid);
        if (_activeDockPointerId is not null || _isInspectorResizeDragging || !point.Properties.IsLeftButtonPressed) return;
        InspectorDockHandle.Focus(FocusState.Pointer);
        if (!InspectorDockHandle.CapturePointer(args.Pointer)) return;
        _activeDockPointerId = args.Pointer.PointerId;
        _dockDragStart = point.Position;
        _dockDragMoved = false;
        args.Handled = true;
    }

    private void InspectorDockHandle_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_activeDockPointerId != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(MainContentGrid);
        if (!point.Properties.IsLeftButtonPressed) { CancelInspectorDockDrag(); return; }
        _dockDragMoved |= InspectorResizeInteractionPolicy.HasMoved(
            _dockDragStart.X, _dockDragStart.Y, point.Position.X, point.Position.Y, 6);
        if (_dockDragMoved) UpdateInspectorDockPreview(point.Position);
        args.Handled = true;
    }

    private void InspectorDockHandle_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_activeDockPointerId != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(MainContentGrid).Position;
        var moved = _dockDragMoved;
        var target = moved ? InspectorDockLayout.HitTest(point.X, point.Y,
            MainContentGrid.ActualWidth, MainContentGrid.ActualHeight) : null;
        CancelInspectorDockDrag();
        if (target is { } position) DockInspector(position);
        else if (!moved) FlyoutBase.ShowAttachedFlyout(InspectorDockHandle);
        args.Handled = true;
    }

    private void InspectorDockHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_activeDockPointerId == args.Pointer.PointerId) CancelInspectorDockDrag();
    }

    private void CancelInspectorDockDrag()
    {
        if (_activeDockPointerId is null) return;
        _activeDockPointerId = null;
        _dockDragMoved = false;
        InspectorDockOverlay.Visibility = Visibility.Collapsed;
        InspectorDockHandle.ReleasePointerCaptures();
    }

    private void InspectorDockHandle_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (AppShortcuts.Matches(AppShortcutAction.EditorCancel, args.Key))
        {
            CancelInspectorDockDrag();
            args.Handled = true;
        }
        else if (_activeDockPointerId is null && AppShortcuts.Matches(AppShortcutAction.EditorConfirm, args.Key))
        {
            FlyoutBase.ShowAttachedFlyout(InspectorDockHandle);
            args.Handled = true;
        }
    }

    private void InspectorDockMenu_Click(object sender, RoutedEventArgs args)
    {
        InspectorDockFlyout.Hide();
        if (sender is FrameworkElement { Tag: string value } && Enum.TryParse<InspectorDockPosition>(value, out var position))
            DockInspector(position);
    }

    private void UpdateInspectorDockPreview(Windows.Foundation.Point point)
    {
        var width = MainContentGrid.ActualWidth;
        var height = MainContentGrid.ActualHeight;
        var target = InspectorDockLayout.HitTest(point.X, point.Y, width, height);
        InspectorDockOverlay.Visibility = Visibility.Visible;
        InspectorDockLeftTarget.Opacity = target == InspectorDockPosition.Left ? 1 : 0.65;
        InspectorDockRightTarget.Opacity = target == InspectorDockPosition.Right ? 1 : 0.65;
        InspectorDockBottomTarget.Opacity = target == InspectorDockPosition.Bottom ? 1 : 0.65;
        InspectorDockPreview.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        if (target is not { } position) return;
        var extent = ExpandedInspectorExtent(position);
        var bottom = position == InspectorDockPosition.Bottom;
        InspectorDockPreview.Width = bottom ? width : extent;
        InspectorDockPreview.Height = bottom ? extent : height;
        InspectorDockPreview.Margin = new Thickness(position == InspectorDockPosition.Right ? width - extent : 0,
            bottom ? height - extent : 0, 0, 0);
    }
}
