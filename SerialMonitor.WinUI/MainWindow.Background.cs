using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SerialMonitor.WinUI.Infrastructure;
using Windows.Foundation;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private BackgroundImageLayout? _lastAppliedBackgroundImageLayout;

    private void CuteBackgroundCanvas_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateCuteBackgroundLayout();

    private void CuteBackgroundImage_Opened(object sender, RoutedEventArgs args) => UpdateCuteBackgroundLayout();

    private void UpdateCuteBackgroundLayout()
    {
        if (_viewModel is null || CuteBackgroundCanvas is null || CuteBackgroundImage is null || CuteBackgroundClip is null) return;
        CuteBackgroundClip.Rect = new Rect(0, 0, CuteBackgroundCanvas.ActualWidth, CuteBackgroundCanvas.ActualHeight);
        var source = CuteBackgroundImage.Source as BitmapSource;
        var layout = BackgroundImageLayout.Calculate(CuteBackgroundCanvas.ActualWidth, CuteBackgroundCanvas.ActualHeight,
            source?.PixelWidth ?? 0, source?.PixelHeight ?? 0, _viewModel.BackgroundPosition);
        if (layout == _lastAppliedBackgroundImageLayout) return;
        _lastAppliedBackgroundImageLayout = layout;
        Canvas.SetLeft(CuteBackgroundImage, layout?.Left ?? 0);
        Canvas.SetTop(CuteBackgroundImage, layout?.Top ?? 0);
        CuteBackgroundImage.Width = layout?.Width ?? 0;
        CuteBackgroundImage.Height = layout?.Height ?? 0;
        // LayoutUpdated sends the final arranged rectangle to xterm through
        // the existing coalesced update path, without reloading either image.
    }
}
