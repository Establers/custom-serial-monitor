using System.Collections;
using Windows.Foundation;

public static partial class StartupHook
{
    private static async Task CheckSensorNarrowLayout(object mainModel, string suffix)
    {
        var model = P(mainModel, "Sensor")!;
        await (Task)Call(model, "LoadExampleAsync")!;
        var view = New("SerialMonitor.WinUI.Views.ModbusSensorView");
        var children = P(Root, "Children")!;
        Call(children, "Clear");
        S(view, "DataContext", model); S(view, "Height", 320d);
        Call(children, "Add", view);
        Rect Bounds(object element) => (Rect)Call(Call(element, "TransformToVisual", view)!, "TransformBounds",
            new Rect(0, 0, N(element, "ActualWidth"), N(element, "ActualHeight")))!;
        foreach (var height in new[] { 240d, 320 })
        foreach (var width in new[] { 320d, 339, 340, 350, 560, 599, 600, 740, 1080, 350 })
        {
            S(view, "Width", width); S(view,"Height",height); await Settle(view);
            Case = "sensor-narrow-" + width + "x" + height + suffix;
            var summary = Field(view, "ProductSummaryText");
            var selector = Field(view, "ProductSelector");
            var list = Field(view, "PointList");
            var scroll = Field(view, "PointScroll");
            var table = Field(view, "PointTable");
            var summarySlot = (Rect)FindType("Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation")
                .GetMethod("GetLayoutSlot")!.Invoke(null, [summary])!;
            if (N(summary, "ActualHeight") > 24 || summarySlot.Width < width - 14)
                Issue("summary does not retain a full-width single line");
            if (N(selector, "ActualWidth") < 190 || N(selector, "ActualWidth") > 481)
                Issue("product selector was squeezed or exceeds the shared maximum");
            var productLabel = Tree(Field(view,"ProductFields")).First(e => e.GetType().Name == "TextBlock" && P(e,"Text")?.ToString() == "제품");
            var management = Field(view,"ManagementButton");
            var labelGap = Bounds(selector).Left - Bounds(productLabel).Right;
            var managementGap = Bounds(management).Left - Bounds(selector).Right;
            var helpGap = Bounds(Field(view,"HelpActions")).Left - Bounds(Field(view,"ValueActions")).Right;
            if (Math.Abs(labelGap - 5) > 2 || Math.Abs(managementGap - 5) > 2) Issue("product controls drifted apart");
            if (width >= 600 && Math.Abs(Bounds(Field(view,"RunActions")).Left - Bounds(management).Right - 5) > 2)
                Issue("response actions drifted away from product management");
            if (width >= 340 && Math.Abs(helpGap - 5) > 2) Issue("help actions drifted away from value actions");
            foreach (var button in Tree(view).Where(e => e.GetType().Name == "Button" && Visible(e)))
            {
                var bounds = Bounds(button);
                if (bounds.Left < -1 || bounds.Right > width + 1) Issue("button outside view: " + P(button, "Content"));
                if (P(button, "Content") is string label && MeasureText(button, label, double.PositiveInfinity).Width > N(button,"ActualWidth") - 10)
                    Issue("button label squeezed: " + label);
            }
            // At 240 DIP height, retain at least three 26-DIP rows except at the narrowest width.
            if (N(list, "ActualHeight") < height - (width < 340 ? 176 : 160)) Issue("toolbar consumed the value list");
            if (N(table, "ActualWidth") < 520) Issue("value columns collapsed");
            var first = ((IEnumerable)P(model, "Points")!).Cast<object>().First();
            S(list, "SelectedItem", first); await Settle(view);
            if (!ReferenceEquals(P(model, "SelectedPoint"), first)) Issue("selection binding lost");
            var texts = Tree(list).Where(e => e.GetType().Name == "TextBlock" && Visible(e)).ToArray();
            var name = texts.First(e => P(e,"Text")?.ToString() == P(first,"Name")?.ToString());
            var value = texts.First(e => P(e,"Text")?.ToString() == P(first,"CurrentValue")?.ToString());
            var rowGrid = FindType("Microsoft.UI.Xaml.Media.VisualTreeHelper").GetMethod("GetParent")!.Invoke(null, [name])!;
            var nameColumn = ((IEnumerable)P(rowGrid,"ColumnDefinitions")!).Cast<object>().First();
            if (N(nameColumn,"ActualWidth") < 140 || Bounds(value).Right > width + 1)
                Issue("first item name/value not usable before horizontal scrolling");
            var header = Field(view, "PointHeader");
            var valueHeader = Tree(header).First(e => P(e,"Text")?.ToString() == P(model,"ValueColumnHeader")?.ToString());
            if (Math.Abs(Bounds(valueHeader).Left - Bounds(value).Left) > 2) Issue("header/value columns misaligned");
            Call(scroll, "ChangeView", (double?)N(scroll,"ScrollableWidth"), null, null, true);
            await Settle(view);
            if (Math.Abs(N(scroll,"HorizontalOffset") - N(scroll,"ScrollableWidth")) > 2) Issue("cannot reach remaining columns");
            if (Math.Abs(Bounds(valueHeader).Left - Bounds(value).Left) > 2) Issue("header and rows scroll separately");
            Call(scroll, "ChangeView", (double?)0, null, null, true);
            await Settle(view);
            var vertical = Tree(list).First(e => e.GetType().Name == "ScrollViewer");
            Call(vertical,"ChangeView",null,(double?)N(vertical,"ScrollableHeight"),null,true);
            await Settle(view);
            if (Math.Abs(N(vertical,"VerticalOffset") - N(vertical,"ScrollableHeight")) > 2) Issue("last row cannot be reached");
            Call(vertical,"ChangeView",null,(double?)0,null,true); await Settle(view);
            Results.Add(new { name = Case, width, height = N(view,"ActualHeight"), summaryHeight = N(summary,"ActualHeight"),
                productWidth = N(selector,"ActualWidth"), listHeight = N(list,"ActualHeight"), tableWidth = N(table,"ActualWidth"),
                summaryWidth = summarySlot.Width, nameColumnWidth = N(nameColumn,"ActualWidth"), valueRight = Bounds(value).Right,
                labelGap, managementGap, helpGap,
                horizontalRange = N(scroll,"ScrollableWidth"), verticalRange = N(vertical,"ScrollableHeight") });
            Save();
        }
        Call(children, "Clear");
    }
}
