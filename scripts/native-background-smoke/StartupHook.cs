using System.Reflection;
using System.Text.Json;
using Windows.Foundation;

// Test-only child process hook. Exit without the app's shutdown/save path so
// test preferences never overwrite the shared user profile.
public static class StartupHook
{
    private static string Output => Environment.GetEnvironmentVariable("SERIAL_BG_NATIVE_RESULT")!;
    public static void Initialize() => _ = Task.Run(PollAsync);

    private static async Task PollAsync()
    {
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until)
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Microsoft.UI.Xaml.Application", false)).FirstOrDefault(t => t is not null);
                var app = type?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var window = app?.GetType().GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(app);
                if (window is not null)
                {
                    var queue = window.GetType().GetProperty("DispatcherQueue")!.GetValue(window)!;
                    var method = queue.GetType().GetMethods().First(m => m.Name == "TryEnqueue" && m.GetParameters().Length == 1);
                    Action callback = () => _ = DriveAsync(window);
                    var handler = Delegate.CreateDelegate(method.GetParameters()[0].ParameterType, callback.Target, callback.Method);
                    method.Invoke(queue, [handler]);
                    return;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException("No application window");
        }
        catch (Exception error) { await File.WriteAllTextAsync(Output, error.ToString()); Environment.Exit(2); }
    }

    private static async Task<string> Script(object core, string script) => await
        ((IAsyncOperation<string>)core.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(core, [script])!).AsTask();

    private static async Task SnapshotAsync(object core, object window, object vm, string name)
    {
        await Task.Delay(1200);
        var state = await Script(core, "JSON.stringify({source:document.getElementById('terminalBackground').src,loaded:document.getElementById('terminalBackground').naturalWidth,visible:document.documentElement.dataset.terminalBackground,opacity:getComputedStyle(document.getElementById('terminalBackground')).opacity,theme:terminal.options.theme.background,text:terminal.buffer.active.getLine(0).translateToString(true)})");
        var screenshot = await ((IAsyncOperation<string>)core.GetType().GetMethod("CallDevToolsProtocolMethodAsync")!.Invoke(core, ["Page.captureScreenshot", "{\"format\":\"png\"}"])!).AsTask();
        var bytes = Convert.FromBase64String(JsonDocument.Parse(screenshot).RootElement.GetProperty("data").GetString()!);
        await File.WriteAllBytesAsync(Output + "." + name + ".png", bytes);
        await File.WriteAllTextAsync(Output + "." + name + ".json", JsonSerializer.Serialize(new {
            state = JsonSerializer.Deserialize<string>(state),
            path = window.GetType().GetField("_lastAppliedCuteBackgroundPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window),
            error = vm.GetType().GetProperty("LastXtermLayoutError")!.GetValue(vm)
        }));
    }

    private static async Task DriveAsync(object window)
    {
        try
        {
            var vm = window.GetType().GetField("_viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!(bool)window.GetType().GetField("_isXtermReady", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)! ||
                (bool)vm.GetType().GetProperty("IsBusy")!.GetValue(vm)!)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("Terminal not ready");
                await Task.Delay(100);
            }
            var webView = window.GetType().GetField("XtermLogWebView", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var core = webView.GetType().GetProperty("CoreWebView2")!.GetValue(webView)!;
            vm.GetType().GetProperty("CuteBackgroundMode")!.SetValue(vm, true);
            vm.GetType().GetProperty("CuteBackgroundImagePath")!.SetValue(vm, "");
            vm.GetType().GetProperty("XtermBackgroundTransparencyPercent")!.SetValue(vm, 50);
            // Put the test line in the real retained model, so an outstanding
            // startup rerender cannot clear a browser-only injected line.
            var log = vm.GetType().GetProperty("Log")!.GetValue(vm)!;
            var lineType = vm.GetType().Assembly.GetType("SerialMonitor.WinUI.Models.LogLine")!;
            var rx = lineType.GetMethod("Rx")!;
            var arguments = rx.GetParameters().Select(p => p.DefaultValue).ToArray();
            arguments[0] = "Native WinUI background test - opaque text";
            var lines = Array.CreateInstance(lineType, 1);
            lines.SetValue(rx.Invoke(null, arguments), 0);
            log.GetType().GetMethod("AddRange")!.Invoke(log, [lines]);
            await SnapshotAsync(core, window, vm, "bundled-50");
            var custom = Environment.GetEnvironmentVariable("SERIAL_BG_NATIVE_CUSTOM");
            if (!string.IsNullOrEmpty(custom))
            {
                var customSource = Environment.GetEnvironmentVariable("SERIAL_BG_NATIVE_CUSTOM_SOURCE")!;
                await File.WriteAllBytesAsync(custom, await File.ReadAllBytesAsync(customSource));
                vm.GetType().GetProperty("CuteBackgroundImagePath")!.SetValue(vm, custom);
                await SnapshotAsync(core, window, vm, "custom-50");
                vm.GetType().GetProperty("XtermBackgroundTransparencyPercent")!.SetValue(vm, 100);
                await SnapshotAsync(core, window, vm, "custom-100");
                vm.GetType().GetProperty("XtermBackgroundTransparencyPercent")!.SetValue(vm, 0);
                await SnapshotAsync(core, window, vm, "custom-0");
                vm.GetType().GetProperty("CuteBackgroundImagePath")!.SetValue(vm, "");
                vm.GetType().GetProperty("XtermBackgroundTransparencyPercent")!.SetValue(vm, 50);
                await SnapshotAsync(core, window, vm, "return-bundled-50");
                vm.GetType().GetProperty("CuteBackgroundMode")!.SetValue(vm, false);
                await SnapshotAsync(core, window, vm, "disabled");
            }
            await File.WriteAllTextAsync(Output, "Completed without serial connection or profile save.");
            Environment.Exit(0);
        }
        catch (Exception error) { await File.WriteAllTextAsync(Output, error.ToString()); Environment.Exit(2); }
    }
}
