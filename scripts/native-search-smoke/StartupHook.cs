using System.Collections;
using System.Reflection;
using System.Text.Json;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

// Runs in a separate app process; never connects a device or saves the test profile.
public static class StartupHook
{
    private static string Output => Environment.GetEnvironmentVariable("SERIAL_SEARCH_TEST_RESULT")!;
    private static readonly List<string> Checks = new();
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Field(object target, string name) => target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static object Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
    private static Task InvokeAsync(object target, string name, params object?[] args) => (Task)Call(target, name, args)!;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Checks.Add(name);
    }

    public static void Initialize() => _ = Task.Run(async () =>
    {
        try
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Microsoft.UI.Xaml.Application")).FirstOrDefault(t => t is not null);
                var app = type?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var window = app?.GetType().GetField("_window", Members)?.GetValue(app);
                if (window is not null)
                {
                    var queue = Get(window, "DispatcherQueue");
                    var enqueue = queue.GetType().GetMethods().Single(m => m.Name == "TryEnqueue" && m.GetParameters().Length == 1);
                    Action callback = () => _ = RunAsync(window);
                    enqueue.Invoke(queue, [Delegate.CreateDelegate(enqueue.GetParameters()[0].ParameterType, callback.Target, callback.Method)]);
                    return;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException("Application did not start");
        }
        catch (Exception error) { Finish(error); }
    });

    private static void Finish(Exception? error = null)
    {
        File.WriteAllText(Output, JsonSerializer.Serialize(new { checks = Checks, error = error?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.Exit(error is null ? 0 : 2);
    }

    private static async Task<string> Script(object core, string script) => await
        ((IAsyncOperation<string>)Call(core, "ExecuteScriptAsync", script)!).AsTask();

    private static async Task CheckSelection(object window, object model, int index, int total, int? sourceIndex = null)
    {
        await Task.Delay(250);
        Check(Convert.ToInt64(Get(model, "SearchMatchCount")) == total,
            $"Total = {total} (actual {Get(model, "SearchMatchCount")}; error {Get(model, "LastSearchError")}; status {Get(model, "SearchResultStatusText")})");
        Check(Convert.ToInt64(Get(model, "CurrentSearchMatchIndex")) == index, $"Current match = {index}");
        var row = Get(model, "SelectedSearchResult");
        Check(Convert.ToInt64(Get(row, "MatchIndex")) == index, $"Selected row = {index}");
        var list = Field(window, "SearchResultsListView");
        Check(ReferenceEquals(Get(list, "SelectedItem"), row), "List selection follows search position");
        var container = Call(list, "ContainerFromItem", row);
        Check(container is not null, "Selected row is realized after scrolling");
        var transform = Call(container!, "TransformToVisual", list)!;
        var bounds = (Rect)Call(transform, "TransformBounds", new Rect(0, 0,
            Convert.ToDouble(Get(container!, "ActualWidth")), Convert.ToDouble(Get(container!, "ActualHeight"))))!;
        Check(bounds.Bottom > 0 && bounds.Top < Convert.ToDouble(Get(list, "ActualHeight")), "Selected row is in the viewport");
        var core = Get(Field(window, "XtermLogWebView"), "CoreWebView2");
        var expected = $"HIT {sourceIndex ?? index:0000}";
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var source = await Script(core, "(() => { const p = terminal.getSelectionPosition(); return p ? terminal.buffer.active.getLine(p.start.y)?.translateToString() : ''; })()");
            if (source.Contains(expected)) { Checks.Add($"Terminal source = {expected}"); return; }
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"Terminal selection did not reach {expected}");
    }

    private static void Append(object model, int first, int count, string? payload = null)
    {
        var lineType = model.GetType().Assembly.GetType("SerialMonitor.WinUI.Models.LogLine")!;
        var rx = lineType.GetMethod("Rx")!;
        var lines = Array.CreateInstance(lineType, count);
        for (var i = 0; i < count; i++)
        {
            var args = rx.GetParameters().Select(p => p.DefaultValue).ToArray();
            args[0] = payload ?? $"HIT {first + i:0000} payload";
            lines.SetValue(rx.Invoke(null, args), i);
        }
        Call(Get(model, "Log"), "AddRange", lines);
        Call(model, "MarkSearchResultsStale");
    }

    private static async Task Capture(object element, string name)
    {
        var bitmap = Activator.CreateInstance(element.GetType().Assembly.GetType("Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap")!)!;
        await ((IAsyncAction)bitmap.GetType().GetMethods().Single(m => m.Name == "RenderAsync" && m.GetParameters().Length == 1).Invoke(bitmap, [element])!).AsTask();
        var buffer = await ((IAsyncOperation<IBuffer>)Call(bitmap, "GetPixelsAsync")!).AsTask();
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)(int)Get(bitmap, "PixelWidth"), (uint)(int)Get(bitmap, "PixelHeight"), 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var encoded = new DataReader(stream);
        await encoded.LoadAsync((uint)stream.Size);
        var png = new byte[stream.Size];
        encoded.ReadBytes(png);
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(Output)!, name + ".png"), png);
    }

    private static async Task RunAsync(object window)
    {
        try
        {
            var model = Field(window, "_viewModel");
            await InvokeAsync(model, "InitializeAsync");
            for (var i = 0; !(bool)Field(window, "_isXtermReady") || (bool)Get(model, "IsBusy"); i++)
            {
                if (i > 300) throw new TimeoutException("Terminal not ready");
                await Task.Delay(100);
            }
            Check(!(bool)Get(model, "IsConnected"), "Test process has no device connection");
            if ((bool)Field(window, "_isInspectorCollapsed")) Call(window, "ToggleInspectorCollapsed");
            var tabs = Field(window, "InspectorTabView");
            var searchTab = ((IEnumerable)Get(tabs, "TabItems")).Cast<object>().Single(t => Get(t, "Header").ToString() == "Search");
            Set(tabs, "SelectedItem", searchTab);
            Set(model, "IsSearchCaseSensitive", false);
            Set(model, "IsSearchWholeWord", false);
            Set(model, "IsSearchRegularExpression", false);
            Set(model, "IsTerminalDirectInputEnabled", false);
            var mode = model.GetType().GetProperty("SelectedRxDisplayMode")!;
            mode.SetValue(model, Enum.Parse(mode.PropertyType, "Terminal"));
            Call(Get(model, "Log"), "SetCapacity", 10_000);
            Call(Get(model, "Log"), "SetViewFilter", new object?[] { null });
            await InvokeAsync(model, "ClearScreenAsync");
            await Task.Delay(250);
            Append(model, 1, 300);
            Set(model, "SearchText", "HIT");
            await InvokeAsync(model, "SearchFromInputAsync", false, "native regression");
            await CheckSelection(window, model, 1, 300);
            var rows = ((IEnumerable)Get(model, "SearchResults")).Cast<object>().ToArray();
            await InvokeAsync(model, "JumpToSearchResultAsync", rows[149]);
            Append(model, 301, 50);
            Check(Convert.ToInt64(Get(model, "SearchMatchCount")) == 300, "Incoming logs do not automatically rebuild results");
            await InvokeAsync(model, "RefreshSearchResultsAsync");
            await CheckSelection(window, model, 150, 350);
            // Execute the actual Search button command, including its async gate.
            var command = Get(model, "SearchVisibleLogCommand");
            Call(command, "Execute", new object?[] { null });
            while ((bool)Field(command, "_isExecuting")) await Task.Delay(20);
            await CheckSelection(window, model, 1, 350);
            await InvokeAsync(model, "FindNextFromShortcutAsync", "native F3");
            await CheckSelection(window, model, 2, 350);
            await InvokeAsync(model, "SearchFromInputAsync", true, "native Shift+Enter");
            await CheckSelection(window, model, 350, 350);
            Append(model, 351, 1_000);
            await InvokeAsync(model, "SearchFromInputAsync", true, "native Shift+Enter");
            await CheckSelection(window, model, 1_350, 1_350);
            Append(model, 1_351, 50);
            await InvokeAsync(model, "RefreshSearchResultsAsync");
            await CheckSelection(window, model, 1_350, 1_400);
            Check(Convert.ToInt32(Get(model, "SearchResultPageNumber")) == 2, "Refresh keeps the selected log on a later page");
            Set(model, "IsSearchCaseSensitive", true);
            await InvokeAsync(model, "RefreshSearchResultsAsync");
            await CheckSelection(window, model, 1, 1_400);
            Check(Convert.ToInt32(Get(model, "SearchResultPageNumber")) == 1, "Changed conditions restart on page one");
            Set(model, "CuteBackgroundMode", false);
            var theme = model.GetType().GetProperty("SelectedAppTheme")!;
            theme.SetValue(model, Enum.Parse(theme.PropertyType, "Light"));
            foreach (var width in new[] { 340d, 560d })
            {
                Set(tabs, "Width", width);
                await Task.Delay(350);
                Check(Math.Abs(Convert.ToDouble(Get(tabs, "ActualWidth")) - width) < 1, $"Inspector width = {width}");
                await Capture(tabs, $"search-{width}");
            }
            await Capture(Field(window, "Root"), "search-window");
            await InvokeAsync(model, "ClearScreenAsync");
            Append(model, 1, 300);
            await InvokeAsync(model, "SearchFromInputAsync", false, "native trim setup");
            rows = ((IEnumerable)Get(model, "SearchResults")).Cast<object>().ToArray();
            await InvokeAsync(model, "JumpToSearchResultAsync", rows[149]);
            Call(Get(model, "Log"), "SetCapacity", 200);
            Append(model, 301, 50);
            await InvokeAsync(model, "RefreshSearchResultsAsync");
            await CheckSelection(window, model, 1, 200, sourceIndex: 151);
            Set(model, "SearchText", "NO_MATCH_IN_THIS_FIXTURE");
            await InvokeAsync(model, "SearchFromInputAsync", false, "native empty search");
            await Task.Delay(350);
            Check(Convert.ToInt64(Get(model, "SearchMatchCount")) == 0, "Empty search clears result count");
            var core = Get(Field(window, "XtermLogWebView"), "CoreWebView2");
            Check(await Script(core, "terminal.hasSelection()") == "false", "Empty search clears old source highlight");
            await InvokeAsync(model, "ClearScreenAsync");
            var longMessage = new string('x', 500) + " context ERROR device response timeout";
            Append(model, 1, 1, longMessage);
            // Search displayed data, not a fixture still queued behind Clear.
            var rendered = false;
            for (var attempt = 0; attempt < 50 && !rendered; attempt++)
            {
                rendered = await Script(core,
                    "Array.from({length: terminal.buffer.active.length}, (_, i) => terminal.buffer.active.getLine(i).translateToString()).join('').includes('ERROR')") == "true";
                if (!rendered) await Task.Delay(100);
            }
            Check(rendered, "Long fixture has reached the terminal renderer");
            Set(model, "SearchText", "ERROR");
            await InvokeAsync(model, "SearchFromInputAsync", false, "native long preview");
            await Task.Delay(350);
            var preview = Get(model, "SelectedSearchResult");
            Check(Get(preview, "MessageText").ToString() == longMessage, "Long preview keeps the complete tooltip payload");
            Check(Get(preview, "MessagePreview").ToString()!.IndexOf("ERROR", StringComparison.Ordinal) is >= 0 and <= 13,
                "Long preview places the match near the start");
            var sourceSelection = await Script(core, "terminal.getSelection()");
            Check(sourceSelection == "\"ERROR\"",
                $"Long preview selects the original source offset (actual {sourceSelection}; {Get(model, "LastXtermSearchError")})");
            await Capture(tabs, "search-long-message");
            Finish();
        }
        catch (Exception error) { Finish(error); }
    }
}
