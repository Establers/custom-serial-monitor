using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class ResizeHandleControl : ContentControl
{
    private bool _horizontal;

    public void SetHorizontal(bool horizontal)
    {
        if (_horizontal == horizontal) return;
        _horizontal = horizontal;
        ProtectedCursor = InputSystemCursor.Create(horizontal
            ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
    }

    public ResizeHandleControl()
    {
        IsTabStop = false;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}
