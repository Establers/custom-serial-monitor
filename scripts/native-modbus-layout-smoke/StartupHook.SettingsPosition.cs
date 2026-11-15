using Windows.Foundation;

public static partial class StartupHook
{
    private static async Task CheckSettingsFlyoutPosition(object mainModel, string suffix)
    {
        var monitor = P(mainModel, "Modbus")!;
        Call(monitor, "ApplySettings", New("SerialMonitor.WinUI.Models.ModbusMonitorSettings"));
        foreach (var lowerRight in new[] { false, true })
        {
            Case = "settings-position-" + (lowerRight ? "lower-right" : "upper-left") + suffix;
            var view = New("SerialMonitor.WinUI.Views.ModbusRtuView");
            var children = P(Root, "Children")!;
            Call(children, "Clear");
            S(view, "DataContext", monitor);
            S(view, "Width", Math.Min(560, N(Root, "ActualWidth")));
            S(view, "Height", 100);
            S(view, "HorizontalAlignment", lowerRight ? "Right" : "Left");
            S(view, "VerticalAlignment", "Top");
            S(view, "Margin", New("Microsoft.UI.Xaml.Thickness", 0d, lowerRight ? N(Root, "ActualHeight") * .78 : 24d, 0d, 0d));
            Call(children, "Add", view);
            await Settle(view);
            var flyout = Field(view, "SettingsFlyout");
            var content = P(flyout, "Content")!;
            // Open once to materialize the template before collecting controls.
            Call(flyout, "ShowAt", Field(view, "SettingsButton"));
            try
            {
                await Task.Delay(350);
                var expanders = Tree(content).Where(e => e.GetType().Name == "Expander").ToArray();
                if (expanders.Length != 2) throw new InvalidOperationException("Read settings must expose both expandable sections.");
                Rect Bounds() => (Rect)Call(Call(content, "TransformToVisual", Root)!, "TransformBounds",
                    new Rect(0, 0, N(content, "ActualWidth"), N(content, "ActualHeight")))!;
                var initial = Bounds();
                var measurements = new List<object>();
                async Task Check(string state)
                {
                    await Task.Delay(350);
                    await Settle(content);
                    var current = Bounds();
                    var dx = current.X - initial.X;
                    var dy = current.Y - initial.Y;
                    if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 ||
                        Math.Abs(current.Width - initial.Width) > 1 || Math.Abs(current.Height - initial.Height) > 1)
                        Issue($"read settings moved/resized at {state}: dx={dx:F1}, dy={dy:F1}, size={current.Width:F1}x{current.Height:F1} (initial {initial.Width:F1}x{initial.Height:F1})");
                    measurements.Add(new { state, dx, dy, width = current.Width, height = current.Height });
                }
                foreach (var index in new[] { 0, 1 })
                {
                    S(expanders[index], "IsExpanded", true);
                    await Check("expand-" + index);
                    S(expanders[index], "IsExpanded", false);
                    await Check("collapse-" + index);
                }
                foreach (var expander in expanders) S(expander, "IsExpanded", true);
                await Check("expand-both");
                var scrolls = await CheckScrolls(content);
                Results.Add(new { name = Case, width = initial.Width, height = initial.Height, measurements, scrolls });
                Save();
            }
            finally { Call(flyout, "Hide"); await Task.Delay(100); }
        }
    }
}
