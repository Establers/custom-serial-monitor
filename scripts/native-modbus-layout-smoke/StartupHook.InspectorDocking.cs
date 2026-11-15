using System.Reflection;
using Windows.Foundation;
using Windows.Graphics;

public static partial class StartupHook
{
    private static async Task RunInspectorPersistence(object window)
    {
        var type = window.GetType();
        Task InvokeTask(string name) => (Task)type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        await InvokeTask("InitializeInspectorLayoutAsync");
        var panel = Field(window, "InspectorPanel");
        var dock = type.GetMethod("DockInspector", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var resize = type.GetMethod("CommitInspectorResize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var toggle = type.GetMethod("ToggleInspectorCollapsed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Case = Environment.GetEnvironmentVariable("SERIAL_MODBUS_LAYOUT_FILTER")!;
        if (Case == "inspector-save")
        {
            dock.Invoke(window, [Enum.Parse(dock.GetParameters()[0].ParameterType, "Bottom")]);
            resize.Invoke(window, [187.5d]);
            await Settle(panel);
            dock.Invoke(window, [Enum.Parse(dock.GetParameters()[0].ParameterType, "Right")]);
            resize.Invoke(window, [421.25d]);
        }
        else if (Case is "inspector-restore" or "inspector-restore-collapsed")
        {
            await Settle(panel);
            if (Field(window, "_inspectorDockPosition").ToString() != "Right" ||
                Math.Abs((double)Field(window, "_lastExpandedInspectorHeight") - 187.5) > 1 ||
                Math.Abs((double)Field(window, "_lastExpandedInspectorWidth") - 421.25) > 1)
                Issue("A fresh process did not restore the last position and both saved sizes.");
            var collapsed = Case == "inspector-restore-collapsed";
            if ((bool)Field(window, "_isInspectorCollapsed") != collapsed) Issue("A fresh process did not restore collapse state.");
            if (!collapsed && Math.Abs(N(panel, "ActualWidth") - 421.25) > 1) Issue("Saved width was not applied to the visible panel.");
            if (!collapsed) toggle.Invoke(window, null); // The third process must restore this collapsed state.
            else
            {
                toggle.Invoke(window, null);
                dock.Invoke(window, [Enum.Parse(dock.GetParameters()[0].ParameterType, "Bottom")]);
                await Settle(panel);
                if (Math.Abs(N(panel, "ActualHeight") - 187.5) > 1) Issue("Restoring the bottom dock lost its independent height.");
            }
        }
        else throw new InvalidOperationException("Unknown inspector persistence fixture.");
        await InvokeTask("FlushInspectorLayoutAsync");
        Results.Add(new { name = Case, width = N(panel, "ActualWidth"), height = N(panel, "ActualHeight") });
    }

    private static async Task RunInspectorDocking(object window)
    {
        await (Task)window.GetType().GetMethod("InitializeInspectorLayoutAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        var workspace = Field(window, "MainContentGrid");
        var panel = Field(window, "InspectorPanel");
        var tabs = Field(window, "InspectorTabView");
        var log = Field(window, "LogPanel");
        var webView = Field(window, "XtermLogWebView");
        var model = Field(window, "_viewModel");
        var tabItems = ((IEnumerable<object>)P(tabs, "TabItems")!).ToArray();
        var contents = tabItems.Select(tab => P(tab, "Content")).ToArray();
        var dock = window.GetType().GetMethod("DockInspector", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var toggle = window.GetType().GetMethod("ToggleInspectorCollapsed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var resize = window.GetType().GetMethod("CommitInspectorResize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Rect Bounds(object element) => (Rect)Call(Call(element, "TransformToVisual", workspace)!,
            "TransformBounds", new Rect(0, 0, N(element, "ActualWidth"), N(element, "ActualHeight")))!;

        void CheckControls(object content, int tabIndex, Rect panelBounds)
        {
            foreach (var scroll in Tree(content).Where(e => Visible(e) && e.GetType().Name == "ScrollViewer" &&
                FindType("SerialMonitor.WinUI.Infrastructure.HorizontalScrollInset").GetMethod("GetEnabled")!.Invoke(null, [e]) is true))
                if (N(scroll, "ScrollableWidth") > 0.5 && N(P(scroll, "Padding")!, "Bottom") < 12)
                    Issue("Horizontal scrollbar overlaps compact content.");
            foreach (var control in Tree(content).Where(e => Visible(e) &&
                new[] { "Button", "TextBox", "ComboBox", "CheckBox" }.Contains(e.GetType().Name)))
            {
                var bounds = Bounds(control);
                if (bounds.Left >= panelBounds.Left - 1 && bounds.Right <= panelBounds.Right + 1) continue;
                var parent = FindType("Microsoft.UI.Xaml.Media.VisualTreeHelper").GetMethod("GetParent")!;
                var scrollable = false;
                for (var ancestor = parent.Invoke(null, [control]); ancestor is not null && !ReferenceEquals(ancestor, panel);
                     ancestor = parent.Invoke(null, [ancestor]))
                    if (FindType("Microsoft.UI.Xaml.Controls.ScrollViewer").IsInstanceOfType(ancestor) &&
                        P(ancestor, "HorizontalScrollBarVisibility")?.ToString() is "Auto" or "Visible")
                        scrollable = true;
                if (!scrollable) Issue($"Tab {tabIndex} {control.GetType().Name} {P(control, "Name")} exceeds panel width without horizontal scrolling.");
            }
        }

        foreach (var size in new[] { new SizeInt32(1200, 800), new SizeInt32(800, 600) })
        {
            Call(P(window, "AppWindow")!, "Resize", size);
            await Settle(workspace);
            foreach (var position in new[] { "Bottom", "Right", "Left", "Bottom" })
            {
                Case = $"inspector-{position}-{size.Width}x{size.Height}";
                var selected = P(tabs, "SelectedItem");
                dock.Invoke(window, [Enum.Parse(dock.GetParameters()[0].ParameterType, position)]);
                await Settle(workspace);
                if (!ReferenceEquals(P(tabs, "SelectedItem"), selected)) Issue("Docking changed the selected tab.");
                if (!ReferenceEquals(webView, Field(window, "XtermLogWebView")) || !ReferenceEquals(model, Field(window, "_viewModel")))
                    Issue("Docking recreated the terminal or model.");
                var panelBounds = Bounds(panel);
                var logBounds = Bounds(log);
                // The preview must not move live controls; all cancellation paths
                // share the same capture/overlay cleanup method.
                window.GetType().GetField("_activeDockPointerId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, (uint?)123);
                window.GetType().GetMethod("UpdateInspectorDockPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new Point(N(workspace, "ActualWidth") - 10, N(workspace, "ActualHeight") / 2)]);
                await Settle(workspace);
                if (P(Field(window, "InspectorDockOverlay"), "Visibility")?.ToString() != "Visible" || Bounds(panel) != panelBounds)
                    Issue("Dock preview moved live controls or disappeared before cancellation.");
                window.GetType().GetMethod("CancelInspectorDockDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                await Settle(workspace);
                if (P(Field(window, "InspectorDockOverlay"), "Visibility")?.ToString() != "Collapsed" || Bounds(panel) != panelBounds ||
                    window.GetType().GetField("_activeDockPointerId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is not null)
                    Issue("Cancellation left an overlay, capture state or changed layout.");
                if (panelBounds.Left < -1 || panelBounds.Top < -1 || panelBounds.Right > N(workspace, "ActualWidth") + 1 ||
                    panelBounds.Bottom > N(workspace, "ActualHeight") + 1) Issue("Docked panel is outside the workspace.");
                if (position == "Left" && panelBounds.Right > logBounds.Left + 1 ||
                    position == "Right" && panelBounds.Left < logBounds.Right - 1 ||
                    position == "Bottom" && panelBounds.Top < logBounds.Bottom - 1) Issue("Docked panel overlaps the terminal.");
                if (logBounds.Width < 279 || logBounds.Height < 179) Issue("Docking leaves too little terminal space.");
                foreach (var (tab, i) in tabItems.Select((tab, i) => (tab, i)))
                {
                    if (!ReferenceEquals(P(tab, "Content"), contents[i])) Issue("Docking recreated tab content.");
                    S(tabs, "SelectedItem", tab);
                    await Settle(panel);
                    if (!Visible(Field(window, "InspectorDockHandle"))) Issue("Move handle is inaccessible on a selected tab.");
                    if (N(contents[i]!, "ActualWidth") > N(panel, "ActualWidth") + 1)
                        Issue($"Tab {i} content exceeds the dock width without a viewport.");
                    CheckControls(contents[i]!, i, panelBounds);
                    if (contents[i]!.GetType().Name == "ProtocolView")
                    {
                        var protocol = P(contents[i]!, "DataContext")!;
                        foreach (var presentation in new[] { 2, 3, 4, 1 })
                        {
                            S(protocol, "SelectedPresentationIndex", presentation);
                            await Settle(panel);
                            CheckControls(contents[i]!, i, panelBounds);
                        }
                    }
                }
                S(tabs, "SelectedItem", selected);
                toggle.Invoke(window, null);
                await Settle(workspace);
                if (P(panel, "Visibility")?.ToString() != "Collapsed" || !Visible(Field(window, "InspectorResizeHandle")))
                    Issue("Collapsed panel has no restore handle.");
                toggle.Invoke(window, null);
                await Settle(workspace);
                if (!Visible(panel)) Issue("Panel did not restore.");
                resize.Invoke(window, [position == "Bottom" ? 160d : 350d]);
                await Settle(workspace);
                Results.Add(new { name = Case, width = size.Width, height = size.Height,
                    panel = Bounds(panel), terminal = Bounds(log), tabsChecked = tabItems.Length });
            }
        }
    }
}
