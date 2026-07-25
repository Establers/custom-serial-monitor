using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class ResizeHandleControl : ContentControl
{
    public ResizeHandleControl()
    {
        IsTabStop = false;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}
