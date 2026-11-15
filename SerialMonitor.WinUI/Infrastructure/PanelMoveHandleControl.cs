using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class PanelMoveHandleControl : ContentControl
{
    public PanelMoveHandleControl()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }
}
