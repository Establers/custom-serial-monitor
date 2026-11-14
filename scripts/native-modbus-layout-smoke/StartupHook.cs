using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.Graphics;

// Runs native layout assertions in a disposable portable-app child process.
// No desktop automation, input injection, COM connection or profile saves.
public static class StartupHook
{
    private static string Output => Environment.GetEnvironmentVariable("SERIAL_MODBUS_LAYOUT_RESULT")!;
    private static readonly List<object> Results = [];
    private static readonly List<string> Issues = [];
    private static readonly HashSet<string> SeenIssues = [];
    private static readonly Dictionary<string, object> TrimmedText = [];
    private static string Case = "startup";
    private static object Host = null!, Root = null!;
    private static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(name, false)).First(t => t is not null)!;
    private static object New(string name, params object?[] args) => Activator.CreateInstance(FindType(name), args)!;
    private static object? P(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);
    private static double N(object o, string name) => System.Convert.ToDouble(P(o, name) ?? 0);
    private static void S(object o, string name, object? value)
    {
        var p = o.GetType().GetProperty(name)!;
        if (value is not null && !p.PropertyType.IsInstanceOfType(value))
            value = p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, value.ToString()!) : System.Convert.ChangeType(value, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType);
        p.SetValue(o, value);
    }
    private static object? Call(object o, string name, params object?[] args)
    {
        var method = o.GetType().GetMethods().First(m => m.Name == name && m.GetParameters().Length == args.Length &&
            m.GetParameters().Select((p, i) => args[i] is null || p.ParameterType.IsInstanceOfType(args[i])).All(x => x));
        return method.Invoke(o, args);
    }
    private static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(o)!;
    private static IEnumerable<object> Tree(object parent)
    {
        yield return parent;
        var helper = FindType("Microsoft.UI.Xaml.Media.VisualTreeHelper");
        var count = (int)helper.GetMethod("GetChildrenCount")!.Invoke(null, [parent])!;
        for (var i = 0; i < count; i++)
            foreach (var child in Tree(helper.GetMethod("GetChild")!.Invoke(null, [parent, i])!)) yield return child;
    }
    private static void Issue(string text)
    {
        if (SeenIssues.Add(Case + ": " + text)) Issues.Add(Case + ": " + text);
    }
    private static void Save(string? fatal = null)
    {
        File.WriteAllText(Output + ".tmp", JsonSerializer.Serialize(new { cases = Results, issues = Issues, trimmedText = TrimmedText.Values, fatal }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(Output + ".tmp", Output, true);
    }
    public static void Initialize() => _ = Task.Run(async () =>
    {
        try
        {
            for (var i = 0; i < 300; i++)
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Microsoft.UI.Xaml.Application", false)).FirstOrDefault(t => t is not null);
                var app = type?.GetProperty("Current")?.GetValue(null);
                var window = app?.GetType().GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(app);
                if (window is not null)
                {
                    var queue = P(window, "DispatcherQueue")!;
                    var enqueue = queue.GetType().GetMethods().First(m => m.Name == "TryEnqueue" && m.GetParameters().Length == 1);
                    Action action = () => _ = Run(window);
                    enqueue.Invoke(queue, [Delegate.CreateDelegate(enqueue.GetParameters()[0].ParameterType, action.Target, action.Method)]);
                    return;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException("Application window was not constructed.");
        }
        catch (Exception e) { Save(e.ToString()); Environment.Exit(2); }
    });

    private static object MonitorSettings()
    {
        var settings = New("SerialMonitor.WinUI.Models.ModbusMonitorSettings");
        S(settings, "UnitId", 247); S(settings, "Function", 3); S(settings, "Address", 65400);
        S(settings, "Quantity", 80); S(settings, "IntervalMs", 60000); S(settings, "TimeoutMs", 10000);
        var itemsProperty = settings.GetType().GetProperty("Items")!;
        var items = (IList)Activator.CreateInstance(itemsProperty.PropertyType)!;
        var itemType = itemsProperty.PropertyType.GetGenericArguments()[0];
        for (var i = 0; i < 20; i++)
        {
            var item = Activator.CreateInstance(itemType)!;
            S(item, "Address", 65400 + i); S(item, "Name", "긴 항목 이름 / long register name " + new string('X', 48) + i);
            items.Add(item);
        }
        S(settings, "Items", items);
        return settings;
    }
    private static object PollingSettings()
    {
        var settings = New("SerialMonitor.WinUI.Models.ModbusPollingSettings");
        var groups = (IList)P(settings, "Groups")!;
        var groupType = groups.GetType().GetGenericArguments()[0];
        for (var i = 0; i < 20; i++)
        {
            var group = Activator.CreateInstance(groupType)!;
            S(group, "Id", "layout-group-" + i); S(group, "Name", "긴 그룹 이름 / long device group " + new string('G', 48) + i);
            S(group, "Settings", MonitorSettings()); groups.Add(group);
        }
        S(settings, "SelectedGroupId", "layout-group-0");
        return settings;
    }
    private static object CommandSettings()
    {
        var type = FindType("SerialMonitor.WinUI.Models.ModbusCommandSettings");
        var settings = JsonSerializer.Deserialize(File.ReadAllText(Environment.GetEnvironmentVariable("SERIAL_MODBUS_LAYOUT_COMMANDS")!), type)!;
        var commands = (IList)P(settings, "Commands")!;
        var first = commands[0]!;
        S(first, "Name", "긴 명령 이름 / long command " + new string('C', 48));
        S(settings, "SelectedCommandId", P(first, "Id"));
        var steps = (IList)P(first, "Steps")!;
        var prototype = steps[0]!;
        while (steps.Count < 32)
        {
            var step = Call(prototype, "Clone")!;
            S(step, "Name", "긴 단계 이름 / long command step " + new string('S', 48) + steps.Count);
            steps.Add(step);
        }
        return settings;
    }
    private static string Lines => string.Join("\n", Enumerable.Range(0, 40).Select(i => $"{i}=긴 상태 이름 / long decoded status {new string('V', 32)}"));
    private static async Task Settle(object element)
    {
        Call(element, "UpdateLayout");
        await Task.Delay(100);
        Call(element, "UpdateLayout");
    }
    private static bool Visible(object e) => P(e, "Visibility")?.ToString() != "Collapsed" && N(e, "ActualWidth") > 0 && N(e, "ActualHeight") > 0;
    private static Size MeasureText(object source, string text, double width)
    {
        var probe = New("Microsoft.UI.Xaml.Controls.TextBlock");
        S(probe, "Text", text);
        foreach (var p in new[] { "FontSize", "FontFamily", "FontWeight", "FontStyle" })
            if (P(source, p) is { } value) S(probe, p, value);
        if (P(source, "TextWrapping") is { } wrapping) S(probe, "TextWrapping", wrapping);
        if (N(source, "LineHeight") > 0) S(probe, "LineHeight", N(source, "LineHeight"));
        Call(probe, "Measure", new Size(width, double.PositiveInfinity));
        return (Size)P(probe, "DesiredSize")!;
    }
    private static void CheckTextAndButtons(object root)
    {
        foreach (var e in Tree(root).Where(Visible))
        {
            string? savedName = null;
            if (e.GetType().Name == "ListViewItem" && P(e, "Content") is { } item)
                savedName = (P(item, "Summary") ?? P(item, "DisplayName")) as string;
            if (e.GetType().Name == "ComboBox" && P(e, "SelectedItem") is { } selected && P(e, "DisplayMemberPath") is string path && path.Length > 0)
                savedName = P(selected, path) as string;
            if (e.GetType().Name == "ComboBox" && P(e, "SelectedItem") is string option &&
                MeasureText(e, option, double.PositiveInfinity).Width > N(e, "ActualWidth") - 40 + 2)
            {
                var tooltip = HasTooltip(e, option);
                TrimmedText[Case + ":" + option] = new { name = Case, text = option, tooltip };
                if (!tooltip) Issue("long selected option has no full-text tooltip: " + option);
            }
            if (savedName?.Contains("긴 ", StringComparison.Ordinal) == true)
            {
                var needed = MeasureText(e, savedName, double.PositiveInfinity);
                var available = N(e, "ActualWidth") - (e.GetType().Name == "ComboBox" ? 40 : 12);
                if (needed.Width > available + 2)
                {
                    var tooltip = HasTooltip(e, savedName) || (e.GetType().Name == "ListViewItem" && Tree(e).Any(child => HasTooltip(child, savedName)));
                    TrimmedText[Case + ":" + savedName] = new { name = Case, text = savedName, tooltip };
                    if (!tooltip) Issue("saved name exceeds its visible row without a full-text tooltip: " + savedName[..Math.Min(90, savedName.Length)]);
                }
            }
            if (e.GetType().Name == "TextBlock" && P(e, "Text") is string text && text.Length > 0)
            {
                // Geometry alone can pass when a headerless control's inherited
                // HeaderTemplate renders its entire view-model as extra text.
                // Check the real visual output as well as its measured bounds.
                if (text.StartsWith("SerialMonitor.WinUI.ViewModels.", StringComparison.Ordinal))
                    Issue("internal view-model type rendered in the UI: " + text);
                if (N(e, "MaxLines") > 0 || P(e, "TextTrimming")?.ToString() != "None")
                {
                    var natural = MeasureText(e, text, double.PositiveInfinity);
                    if (P(e, "IsTextTrimmed") is true || natural.Width > N(e, "ActualWidth") + 2)
                    {
                        var tooltip = HasTooltip(e, text);
                        TrimmedText[Case + ":" + text] = new { name = Case, text, tooltip };
                        if (text.Contains("긴 ", StringComparison.Ordinal) && !tooltip)
                            Issue("ellipsized saved name has no full-text tooltip: " + text[..Math.Min(90, text.Length)]);
                    }
                    continue;
                }
                var wrap = P(e, "TextWrapping")?.ToString() != "NoWrap";
                // ActualWidth is layout-rounded. Avoid wrapping a final glyph
                // again just because the rounded width lost a fraction of a DIP.
                var needed = MeasureText(e, text, wrap ? N(e, "ActualWidth") + 2 : double.PositiveInfinity);
                if (!wrap && needed.Width > N(e, "ActualWidth") + 2)
                    Issue($"text width {N(e, "ActualWidth"):F1} < {needed.Width:F1}: {text[..Math.Min(90, text.Length)]}");
                if (needed.Height > N(e, "ActualHeight") + 2)
                    Issue($"text height {N(e, "ActualHeight"):F1} < {needed.Height:F1}: {text[..Math.Min(90, text.Length)]}");
            }
            if (e.GetType().Name == "Button" && P(e, "Content") is string label && label.Length > 0)
            {
                var needed = MeasureText(e, label, double.PositiveInfinity);
                var padding = P(e, "Padding")!; var border = P(e, "BorderThickness")!;
                var available = N(e, "ActualWidth") - N(padding, "Left") - N(padding, "Right") - N(border, "Left") - N(border, "Right");
                if (needed.Width > available + 2) Issue($"button label width {available:F1} < {needed.Width:F1}: {label}");
                if (needed.Height > N(e, "ActualHeight") + 2) Issue("button label height: " + label);
            }
        }
    }
    private static bool HasTooltip(object element, string? requiredText = null)
    {
        var tooltip = FindType("Microsoft.UI.Xaml.Controls.ToolTipService").GetMethod("GetToolTip")!;
        var parent = FindType("Microsoft.UI.Xaml.Media.VisualTreeHelper").GetMethod("GetParent")!;
        object? current = element;
        for (var depth = 0; current is not null && depth < 64; depth++, current = parent.Invoke(null, [current]))
            if (tooltip.Invoke(null, [current]) is { } value)
            {
                var text = (P(value, "Content") ?? value).ToString();
                if (!string.IsNullOrWhiteSpace(text) && (requiredText is null || text.Contains(requiredText, StringComparison.Ordinal))) return true;
            }
        return false;
    }
    private static async Task<List<object>> CheckScrolls(object root)
    {
        var measurements = new List<object>();
        foreach (var scroll in Tree(root).Where(e => e.GetType().Name == "ScrollViewer" && Visible(e)).ToArray())
        {
            var name = P(scroll, "Name")?.ToString() ?? "";
            if (N(scroll, "ScrollableWidth") > 2 && P(scroll, "HorizontalScrollBarVisibility")?.ToString() == "Disabled")
                Issue($"horizontal overflow in {name}: {N(scroll, "ScrollableWidth"):F1}");
            if (N(scroll, "ScrollableWidth") > 2 && P(scroll, "HorizontalScrollBarVisibility")?.ToString() is "Auto" or "Visible")
            {
                Call(scroll, "ChangeView", (double?)N(scroll, "ScrollableWidth"), null, null, true);
                await Settle(scroll);
                if (Math.Abs(N(scroll, "HorizontalOffset") - N(scroll, "ScrollableWidth")) > 2) Issue("cannot reach horizontal scroll end in " + name);
                measurements.Add(new { name, horizontal = true, viewport = N(scroll, "ViewportWidth"), extent = N(scroll, "ExtentWidth"), reached = N(scroll, "HorizontalOffset") });
                Call(scroll, "ChangeView", (double?)0, null, null, true);
                await Settle(scroll);
            }
            var end = N(scroll, "ScrollableHeight");
            if (end <= 1) continue;
            Call(scroll, "ChangeView", null, (double?)end, null, true);
            await Settle(scroll);
            if (Math.Abs(N(scroll, "VerticalOffset") - N(scroll, "ScrollableHeight")) > 2)
                Issue($"cannot reach scroll end in {name}: {N(scroll, "VerticalOffset"):F1}/{N(scroll, "ScrollableHeight"):F1}");
            var content = P(scroll, "Content");
            if (content is not null && P(content, "Children") is IEnumerable children)
            {
                var last = children.Cast<object>().LastOrDefault(Visible);
                if (last is not null)
                {
                    var transform = Call(last, "TransformToVisual", scroll)!;
                    var bounds = (Rect)Call(transform, "TransformBounds", new Rect(0, 0, N(last, "ActualWidth"), N(last, "ActualHeight")))!;
                    if (bounds.Bottom > N(scroll, "ActualHeight") + 2)
                        Issue($"last content remains below {name} at scroll end: {bounds.Bottom:F1}/{N(scroll, "ActualHeight"):F1}");
                }
            }
            measurements.Add(new { name, viewport = N(scroll, "ViewportHeight"), extent = N(scroll, "ExtentHeight"), reached = N(scroll, "VerticalOffset") });
            CheckTextAndButtons(root);
            Call(scroll, "ChangeView", null, (double?)0, null, true);
            await Settle(scroll);
        }
        return measurements;
    }
    private static async Task CheckNumericInputs(object root)
    {
        var invalidPaste = Case.Contains("-error", StringComparison.Ordinal);
        foreach (var input in Tree(root).Where(e => e.GetType().Name == "TextBox" && Visible(e) && P(e, "AcceptsReturn") is false).ToArray())
        {
            var text = P(input, "Text")?.ToString() ?? "";
            if (!Regex.IsMatch(text, @"^[+-]?(?:0[xX][0-9A-Fa-f]{1,8}|[0-9]{1,10})$")) continue;
            Call(input, "Focus", Enum.Parse(FindType("Microsoft.UI.Xaml.FocusState"), "Programmatic"));
            await Settle(input);
            var viewport = Tree(input).FirstOrDefault(e => e.GetType().Name == "ScrollViewer" && P(e, "Name")?.ToString() == "ContentElement");
            if (viewport is not null && N(viewport, "ExtentWidth") > N(viewport, "ViewportWidth") + 2)
            {
                // Invalid pastes are not length-limited. Verify the caret can
                // reveal their end instead of requiring an arbitrarily wide field.
                if (invalidPaste && P(input, "IsReadOnly") is false)
                {
                    S(input, "SelectionStart", text.Length); S(input, "SelectionLength", 0);
                    await Settle(input);
                    if (N(viewport, "HorizontalOffset") + 2 < N(viewport, "ScrollableWidth") || P(input, "Text")?.ToString() != text)
                        Issue("invalid numeric paste cannot be reached intact: " + text);
                }
                else Issue($"focused numeric input clips {text}: {N(viewport, "ExtentWidth"):F1}/{N(viewport, "ViewportWidth"):F1}");
            }
        }
    }
    private static List<object> CheckFormGrids(object root)
    {
        var measurements = new List<object>();
        var gridType = FindType("Microsoft.UI.Xaml.Controls.Grid");
        int Cell(object element, string property) => (int)gridType.GetMethod("Get" + property)!.Invoke(null, [element])!;
        Rect Bounds(object element, object relativeTo) => (Rect)Call(Call(element, "TransformToVisual", relativeTo)!,
            "TransformBounds", new Rect(0, 0, N(element, "ActualWidth"), N(element, "ActualHeight")))!;
        double? leftRail = null, formWidth = null;
        foreach (var grid in Tree(root).Where(e => e.GetType().Name == "Grid" && P(e, "Tag")?.ToString() == "ModbusFormGrid" && Visible(e)))
        {
            var columns = ((IEnumerable)P(grid, "ColumnDefinitions")!).Cast<object>().ToArray();
            var left = Bounds(grid, root).Left;
            leftRail ??= left;
            formWidth ??= N(grid, "ActualWidth");
            if (Math.Abs(left - leftRail.Value) > 2) Issue($"form left rail differs: {left:F1}/{leftRail:F1}");
            if (Math.Abs(N(grid, "ActualWidth") - formWidth.Value) > 2) Issue("form right rails do not align");
            if (Math.Abs(N(columns[0], "ActualWidth") - 128) > 1) Issue("form first column is not 128 DIP");
            if (Math.Abs(N(grid, "ColumnSpacing") - 8) > 1 || Math.Abs(N(grid, "RowSpacing") - 4) > 1)
                Issue("form spacing differs from the shared grid");
            var children = ((IEnumerable)P(grid, "Children")!).Cast<object>().Where(Visible).ToArray();
            foreach (var row in children.GroupBy(e => Cell(e, "Row")))
            {
                var tops = row.Select(e => Bounds(e, grid).Top).ToArray();
                if (tops.Max() - tops.Min() > 2) Issue("form row controls do not share a top edge");
                if (row.Key % 2 == 1)
                {
                    var labels = children.Where(e => Cell(e, "Row") == row.Key - 1).ToArray();
                    if (labels.Length > 0 && Math.Abs(tops.Min() - labels.Max(e => Bounds(e, grid).Bottom) - 4) > 1)
                        Issue("form label/input gap is not 4 DIP");
                }
            }
            foreach (var input in children.Where(e => e.GetType().Name is "TextBox" or "ComboBox" or "ToggleSwitch"))
            {
                var column = Cell(input, "Column");
                var label = children.FirstOrDefault(e => e.GetType().Name == "TextBlock" &&
                    Cell(e, "Column") == column && Cell(e, "Row") == Cell(input, "Row") - 1);
                if (label is null) { Issue("form control has no label in the preceding row"); continue; }
                var fieldBounds = Bounds(input, grid); var labelBounds = Bounds(label, grid);
                var expectedLeft = columns.Take(column).Sum(c => N(c, "ActualWidth")) + column * 8;
                if (Math.Abs(fieldBounds.Left - expectedLeft) > 1 || Math.Abs(labelBounds.Left - expectedLeft) > 1)
                    Issue($"form label/input left edges differ at column {column}");
                var span = Cell(input, "ColumnSpan");
                var allocatedWidth = columns.Skip(column).Take(span).Sum(c => N(c, "ActualWidth")) + (span - 1) * 8;
                if (fieldBounds.Width > allocatedWidth + 2)
                    Issue("form input exceeds its allocated column");
                if (P(input, "Header") is not null) Issue("paired form input still has a separate native header");
                if (input.GetType().Name is "TextBox" or "ComboBox" && P(input, "AcceptsReturn") is not true && N(input, "ActualHeight") > 26)
                    Issue($"headerless compact input is taller than 24 DIP: {N(input, "ActualHeight"):F1}");
                var automation = FindType("Microsoft.UI.Xaml.Automation.AutomationProperties").GetMethod("GetName")!.Invoke(null, [input]);
                if (automation?.ToString() != P(label, "Text")?.ToString()) Issue("form input lost its accessible label");
            }
            measurements.Add(new { left, width = N(grid, "ActualWidth"), firstColumn = N(columns[0], "ActualWidth"), fields = children.Count(e => Cell(e, "Row") % 2 == 1) });
        }
        return measurements;
    }
    private static async Task Probe(object root, string name)
    {
        Case = name;
        var before = Issues.Count;
        await Settle(root);
        CheckTextAndButtons(root);
        var formGrids = CheckFormGrids(root);
        if ((name.StartsWith("items-") || name.StartsWith("groups-") || name.StartsWith("commands-") ||
             name.StartsWith("packet-") || name.StartsWith("SettingsFlyout")) && formGrids.Count == 0)
            Issue("expected a shared form grid but none was measured");
        await CheckNumericInputs(root);
        var scrolls = await CheckScrolls(root);
        var surface = Tree(root).FirstOrDefault(e => P(e, "Name")?.ToString() == "BackgroundElement") ?? root;
        if (!ReferenceEquals(surface, root))
        {
            var bounds = (Rect)Call(Call(surface, "TransformToVisual", root)!, "TransformBounds", new Rect(0, 0, N(surface, "ActualWidth"), N(surface, "ActualHeight")))!;
            foreach (var button in Tree(root).Where(e => e.GetType().Name == "Button" && Visible(e) && P(e, "Name")?.ToString() is "PrimaryButton" or "CloseButton"))
            {
                var rect = (Rect)Call(Call(button, "TransformToVisual", root)!, "TransformBounds", new Rect(0, 0, N(button, "ActualWidth"), N(button, "ActualHeight")))!;
                if (rect.Top < bounds.Top - 2 || rect.Bottom > bounds.Bottom + 2 || rect.Left < bounds.Left - 2 || rect.Right > bounds.Right + 2)
                    Issue("dialog footer button lies outside the dialog surface: " + P(button, "Name"));
            }
        }
        Results.Add(new { name, width = N(surface, "ActualWidth"), height = N(surface, "ActualHeight"), rasterizationScale = N(P(root, "XamlRoot")!, "RasterizationScale"), formGrids, scrolls, issues = Issues.Count - before });
        Save();
    }
    private static async Task Dialog(object dialog, string name, Action<object>? configure = null)
    {
        var filter = Environment.GetEnvironmentVariable("SERIAL_MODBUS_LAYOUT_FILTER");
        if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
        S(dialog, "XamlRoot", P(Root, "XamlRoot"));
        configure?.Invoke(P(dialog, "Model")!);
        var operation = Call(dialog, "ShowAsync")!;
        try
        {
            if (name.Contains("advanced", StringComparison.Ordinal))
            {
                await Settle(dialog);
                foreach (var expander in Tree(dialog).Where(e => e.GetType().Name == "Expander")) S(expander, "IsExpanded", true);
                await Task.Delay(350);
            }
            await Probe(dialog, name);
        }
        finally
        {
            Call(dialog, "Hide");
            for (var i = 0; i < 40 && ((IAsyncInfo)operation).Status == AsyncStatus.Started; i++) await Task.Delay(25);
        }
    }
    private static async Task View(object view, string name, object model)
    {
        var children = P(Root, "Children")!;
        Call(children, "Clear");
        S(view, "DataContext", model); S(view, "Width", Math.Min(560, N(Root, "ActualWidth")));
        S(view, "Height", N(Root, "ActualHeight"));
        Call(children, "Add", view);
        await Settle(view);
        await Probe(view, name);
    }
    private static async Task Flyout(object view, string field, string anchor, string name)
    {
        var flyout = Field(view, field);
        Call(flyout, "ShowAt", Field(view, anchor));
        try
        {
            var content = P(flyout, "Content")!;
            await Probe(content, name);
            var expanders = Tree(content).Where(e => e.GetType().Name == "Expander").ToArray();
            foreach (var expander in expanders) S(expander, "IsExpanded", true);
            if (expanders.Length > 0) { await Task.Delay(350); await Probe(content, name + "-expanded"); }
        }
        finally { Call(flyout, "Hide"); await Task.Delay(100); }
    }
    private static object ResponseFrame()
    {
        var bytes = new byte[255]; bytes[0] = 247; bytes[1] = 3; bytes[2] = 250;
        for (var i = 3; i < bytes.Length - 2; i++) bytes[i] = (byte)i;
        ushort crc = 0xFFFF;
        foreach (var value in bytes.AsSpan(0, bytes.Length - 2))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        bytes[^2] = (byte)crc; bytes[^1] = (byte)(crc >> 8);
        return New("SerialMonitor.Core.Protocols.ModbusRtuFrame", 1L, 1L, DateTimeOffset.Now,
            Enum.Parse(FindType("SerialMonitor.Core.Protocols.ProtocolDirection"), "Rx"), new ReadOnlyMemory<byte>(bytes),
            Enum.Parse(FindType("SerialMonitor.Core.Protocols.ModbusFrameKind"), "Response"),
            Enum.Parse(FindType("SerialMonitor.Core.Protocols.ModbusCrcStatus"), "Valid"));
    }
    private static void PopulateRegisters(object monitor)
    {
        var settings = MonitorSettings();
        var definitions = (IList)P(settings, "Items")!;
        var values = Enumerable.Repeat(ushort.MaxValue, 80).ToArray();
        var rows = P(monitor, "Rows")!;
        for (var i = 0; i < values.Length; i++)
        {
            var row = New("SerialMonitor.WinUI.ViewModels.ModbusRegisterRow", 65400 + i, i, 1, 1, i < definitions.Count ? definitions[i] : null);
            row.GetType().GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(row, [values, settings, true, DateTimeOffset.Now]);
            Call(rows, "Add", row);
        }
        monitor.GetType().GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(monitor, ["EmptyVisibility"]);
    }
    private static async Task Run(object mainWindow)
    {
        try
        {
            await Task.Delay(600);
            if (P(Field(mainWindow, "_viewModel"), "IsConnected") is true) throw new InvalidOperationException("Layout tests require a disconnected child instance.");
            Host = New("Microsoft.UI.Xaml.Window");
            Root = New("Microsoft.UI.Xaml.Controls.Grid");
            S(Host, "Content", Root); Call(Host, "Activate");
            foreach (var size in new[] { new SizeInt32(1280, 900), new SizeInt32(800, 600) })
            {
                Call(P(Host, "AppWindow")!, "Resize", size);
                await Settle(Root);
                var suffix = $"-{size.Width}x{size.Height}";
                await Dialog(New("SerialMonitor.WinUI.Views.ModbusItemEditorDialog", MonitorSettings(), null), "items-long" + suffix,
                    m => { S(m, "NameText", "긴 항목 / " + new string('N', 80)); S(m, "ValueNamesText", Lines); });
                await Dialog(New("SerialMonitor.WinUI.Views.ModbusPollingEditorDialog", PollingSettings()), "groups-long" + suffix);
                await Dialog(New("SerialMonitor.WinUI.Views.ModbusCommandEditorDialog", CommandSettings()), "commands-advanced" + suffix,
                    m => { S(m, "IsAdvancedExpanded", true); S(m, "ValueNamesText", Lines); });
                await Dialog(New("SerialMonitor.WinUI.Views.ModbusItemEditorDialog", MonitorSettings(), null), "items-error" + suffix,
                    m => { S(m, "AddressText", "999999"); Call(m, "SaveItem"); });
                await Dialog(New("SerialMonitor.WinUI.Views.ModbusPollingEditorDialog", PollingSettings()), "groups-error" + suffix,
                    m => { S(m, "UnitText", "999999"); Call(m, "SaveGroup"); });
                await Dialog(New("SerialMonitor.WinUI.Views.ModbusCommandEditorDialog", CommandSettings()), "commands-error" + suffix,
                    m => { S(m, "UnitText", "999999"); Call(m, "SaveStep"); });
                foreach (byte function in new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 15, 16, 17, 20, 21, 22, 23, 24, 43, 0 })
                    await Dialog(New("SerialMonitor.WinUI.Views.ModbusPacketBuilderDialog"), "packet-" + function + suffix,
                        m =>
                        {
                            Call(m, "SelectFunction", function); S(m, "UnitText", "247"); S(m, "AddressText", "65400");
                            if (function == 6) S(m, "ValuesText", "65535");
                            if (function == 8) S(m, "DiagnosticDataText", string.Join(" ", Enumerable.Repeat("65535", 125)));
                            if (function == 16) S(m, "ValuesText", string.Join(" ", Enumerable.Repeat("65535", 123)));
                            if (function == 0) S(m, "CustomDataText", string.Join(" ", Enumerable.Repeat("AB", 252)));
                        });
                await Dialog(New("SerialMonitor.WinUI.Views.ModbusPacketBuilderDialog"), "packet-error" + suffix,
                    m => S(m, "UnitText", "999999"));
                await Dialog(New("SerialMonitor.WinUI.Views.ModbusPacketBuilderDialog"), "packet-diagnostic-scalar" + suffix,
                    m => { Call(m, "SelectFunction", (byte)8); S(m, "DiagnosticIndex", 7); });
                await Dialog(New("SerialMonitor.WinUI.Views.ModbusPacketBuilderDialog"), "packet-device-long" + suffix,
                    m => { Call(m, "SelectFunction", (byte)43); S(m, "DeviceAccessIndex", 3); S(m, "ObjectIdText", "255"); });

                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SERIAL_MODBUS_LAYOUT_FILTER")))
                {
                    var mainModel = Field(mainWindow, "_viewModel");
                    var monitor = P(mainModel, "Modbus")!;
                    Call(monitor, "ApplySettings", MonitorSettings());
                    Call(monitor, "ApplySavedCommandSettings", CommandSettings());
                    PopulateRegisters(monitor);
                    S(monitor, "WriteAddressText", "65400");
                    S(monitor, "WriteValuesText", string.Join(" ", Enumerable.Repeat("65535", 80)));
                    var monitorView = New("SerialMonitor.WinUI.Views.ModbusRtuView");
                    await View(monitorView, "monitor" + suffix, monitor);
                    foreach (var entry in new[] { ("SettingsFlyout", "SettingsButton"), ("WriteFlyout", "MonitorMoreButton"), ("CommandsFlyout", "CommandsButton"), ("GuideFlyout", "MonitorMoreButton") })
                        await Flyout(monitorView, entry.Item1, entry.Item2, entry.Item1 + suffix);
                    S(monitor, "WriteModeIndex", 1); S(monitor, "WriteValuesText", "65535");
                    await Flyout(monitorView, "WriteFlyout", "MonitorMoreButton", "WriteFlyout-item" + suffix);
                    var protocol = New("SerialMonitor.WinUI.Views.ProtocolView");
                    var protocolModel = P(mainModel, "Protocol")!;
                    S(protocolModel, "SelectedViewIndex", 1);
                    await View(protocol, "protocol" + suffix, protocolModel);
                    await Flyout(protocol, "FilterFlyout", "PresentationSelector", "filter" + suffix);
                    var interpreter = New("SerialMonitor.WinUI.Views.ModbusValueInterpreterView");
                    var values = New("SerialMonitor.WinUI.ViewModels.ModbusValueInterpreterViewModel");
                    Call(values, "SetFrame", ResponseFrame());
                    await View(interpreter, "values-long" + suffix, values);
                    S(values, "OffsetText", "999999");
                    await Probe(interpreter, "values-error" + suffix);
                    Call(P(Root, "Children")!, "Clear");
                }
            }
            if (P(Field(mainWindow, "_viewModel"), "IsConnected") is true) throw new InvalidOperationException("Layout child unexpectedly connected a serial port.");
            Save();
            Environment.Exit(Issues.Count == 0 ? 0 : 1);
        }
        catch (Exception e) { Save(e.ToString()); Environment.Exit(2); }
    }
}
