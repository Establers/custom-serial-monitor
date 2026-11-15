using System.Reflection;
using Windows.Graphics;

public static partial class StartupHook
{
    private static async Task RunInspectorStability(object window)
    {
        var type = window.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Invoke(string method, params object?[] args) => type.GetMethod(method, flags)!.Invoke(window, args);
        void SetField(string name, object? value) => type.GetField(name, flags)!.SetValue(window, value);
        var dockType = type.GetMethod("DockInspector", flags)!.GetParameters()[0].ParameterType;
        var panel = Field(window, "InspectorPanel");
        var tabs = Field(window, "InspectorTabView");
        var terminal = Field(window, "XtermLogWebView");
        var selected = P(tabs, "SelectedItem");
        Case = "inspector-rapid-docking";
        for (var i = 0; i < 90; i++)
        {
            Invoke("DockInspector", Enum.Parse(dockType, new[] { "Left", "Right", "Bottom" }[i % 3]));
            if (i % 5 == 0) { Invoke("ToggleInspectorCollapsed"); Invoke("ToggleInspectorCollapsed"); }
            await Task.Delay(10);
        }
        await Settle(panel);
        // The shield has its own 110 ms deadline; layout settling alone is 100 ms.
        for (var i = 0; i < 40 && P(Field(window, "XtermResizeShield"), "Visibility")?.ToString() != "Collapsed"; i++)
            await Task.Delay(50);
        if (!ReferenceEquals(terminal, Field(window, "XtermLogWebView")) || !ReferenceEquals(selected, P(tabs, "SelectedItem")))
            Issue("Rapid docking replaced the terminal or selected tab.");
        if (P(Field(window, "XtermResizeShield"), "Visibility")?.ToString() != "Collapsed")
            Issue("Rapid docking left the terminal covered by its resize shield.");
        Results.Add(new { name = Case, transitions = 90 });

        Case = "inspector-slow-terminal-fit-coalescing";
        var originalOperation = Field(window, "_xtermFitOperation");
        var releaseFit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var concurrent = 0;
        var maximumConcurrent = 0;
        Func<bool, CancellationToken, Task> slowFit = async (_, token) =>
        {
            maximumConcurrent = Math.Max(maximumConcurrent, ++concurrent);
            try { if (++calls == 1) await releaseFit.Task.WaitAsync(token); }
            finally { concurrent--; }
        };
        var operationType = FindType("SerialMonitor.WinUI.Infrastructure.LatestRequestAsyncOperation`1").MakeGenericType(typeof(bool));
        var operation = Activator.CreateInstance(operationType, slowFit, CancellationToken.None, null)!;
        SetField("_xtermFitOperation", operation);
        try
        {
            for (var i = 0; i < 100; i++) Invoke("QueueXtermFit");
            await Task.Delay(80);
            for (var i = 0; i < 100; i++) Invoke("QueueXtermFit");
            await Task.Delay(80);
            var completion = (Task)Call(operation, "RunAsync", true)!;
            if (calls != 1) Issue("Resize bursts entered the terminal while a previous fit was still running.");
            releaseFit.SetResult();
            await completion.WaitAsync(TimeSpan.FromSeconds(2));
            if (calls != 2 || maximumConcurrent != 1) Issue("Fit requests were not limited to one active and one latest request.");
        }
        finally { releaseFit.TrySetResult(); SetField("_xtermFitOperation", originalOperation); }
        Results.Add(new { name = Case, requests = 200, calls, maximumConcurrent });

        foreach (var position in new[] { "Left", "Right", "Bottom" })
        {
            Case = $"inspector-interrupted-resize-{position}";
            Invoke("DockInspector", Enum.Parse(dockType, position));
            Call(P(window, "AppWindow")!, "Resize", new SizeInt32(1200, 800));
            await Settle(panel);
            // A second input device/window resize can change the workspace while
            // the splitter still owns capture. No synthetic desktop input is used.
            SetField("_isInspectorResizeDragging", true);
            SetField("_inspectorResizeMoved", true);
            SetField("_activeInspectorResizePointerId", (uint?)123);
            SetField("_pendingInspectorResizeHeight", position == "Bottom" ? 180d : 350d);
            Call(P(window, "AppWindow")!, "Resize", new SizeInt32(800, 600));
            await Settle(panel);
            if ((bool)Field(window, "_isInspectorResizeDragging") || Field(window, "_activeInspectorResizePointerId") is not null)
                Issue("Workspace resize left a stale gesture that blocks docking and terminal fitting.");
            Invoke("FinishInspectorResize", Field(window, "InspectorResizeHandle"), true);
            Invoke("DockInspector", Enum.Parse(dockType, "Bottom"));
            await Settle(panel);
            if (Field(window, "_inspectorDockPosition").ToString() != "Bottom") Issue("Docking did not recover after interruption.");
            Results.Add(new { name = Case, width = N(panel, "ActualWidth"), height = N(panel, "ActualHeight") });
        }

        Case = "inspector-closing-ignores-late-dock";
        SetField("_closeCleanupStarted", true);
        try
        {
            Invoke("DockInspector", Enum.Parse(dockType, "Left"));
            if (Field(window, "_inspectorDockPosition").ToString() != "Bottom")
                Issue("Late input modified layout during shutdown.");
        }
        finally { SetField("_closeCleanupStarted", false); }
        Results.Add(new { name = Case });
    }
}
