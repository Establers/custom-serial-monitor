using System.Reflection;
using System.Text.Json;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

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

    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static async Task CaptureElementAsync(object element, string path)
    {
        var type = element.GetType().Assembly.GetType("Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap")!;
        var bitmap = Activator.CreateInstance(type)!;
        await ((IAsyncAction)type.GetMethods().Single(m => m.Name == "RenderAsync" && m.GetParameters().Length == 1).Invoke(bitmap, [element])!).AsTask();
        var buffer = await ((IAsyncOperation<IBuffer>)type.GetMethod("GetPixelsAsync")!.Invoke(bitmap, null)!).AsTask();
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)(int)type.GetProperty("PixelWidth")!.GetValue(bitmap)!, (uint)(int)type.GetProperty("PixelHeight")!.GetValue(bitmap)!, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var encoded = new DataReader(stream);
        await encoded.LoadAsync((uint)stream.Size);
        var png = new byte[stream.Size];
        encoded.ReadBytes(png);
        await File.WriteAllBytesAsync(path, png);
    }

    private static async Task<double> MeasureAlignmentAsync(object window, byte[] screenshot)
    {
        var image = Field(window, "CuteBackgroundImage");
        var webView = Field(window, "XtermLogWebView");
        double Number(object target, string name) => (double)target.GetType().GetProperty(name)!.GetValue(target)!;
        var transform = image.GetType().GetMethod("TransformToVisual")!.Invoke(image, [webView])!;
        var bounds = (Rect)transform.GetType().GetMethod("TransformBounds")!.Invoke(transform,
            [new Rect(0, 0, Number(image, "ActualWidth"), Number(image, "ActualHeight"))])!;
        var bitmapType = image.GetType().Assembly.GetType("Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap")!;
        var bitmap = Activator.CreateInstance(bitmapType)!;
        var render = bitmapType.GetMethods().Single(m => m.Name == "RenderAsync" && m.GetParameters().Length == 1);
        await ((IAsyncAction)render.Invoke(bitmap, [image])!).AsTask();
        var buffer = await ((IAsyncOperation<IBuffer>)bitmapType.GetMethod("GetPixelsAsync")!.Invoke(bitmap, null)!).AsTask();
        var reference = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(reference);
        var referenceWidth = (int)bitmapType.GetProperty("PixelWidth")!.GetValue(bitmap)!;
        var referenceHeight = (int)bitmapType.GetProperty("PixelHeight")!.GetValue(bitmap)!;

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(screenshot);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var browserPixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;
        var viewportWidth = Number(webView, "ActualWidth");
        var viewportHeight = Number(webView, "ActualHeight");
        double error = 0;
        int samples = 0;
        // Compare real XAML image pixels against the WebView crop, excluding
        // the text/cursor row and scrollbar. Allow small resampling differences.
        for (var y = 48; y < height - 8; y += Math.Max(1, height / 40))
        for (var x = 8; x < width - 32; x += Math.Max(1, width / 80))
        {
            var localX = (x + 0.5) / width * viewportWidth - bounds.X;
            var localY = (y + 0.5) / height * viewportHeight - bounds.Y;
            // Stay inside the rendered image when sampling edge pixels.
            if (localX < 1 || localY < 1 || localX >= bounds.Width - 1 || localY >= bounds.Height - 1) continue;
            var rx = (int)Math.Clamp(localX / bounds.Width * referenceWidth, 0, referenceWidth - 1);
            var ry = (int)Math.Clamp(localY / bounds.Height * referenceHeight, 0, referenceHeight - 1);
            for (var channel = 0; channel < 3; channel++)
            {
                error += Math.Abs(browserPixels[(y * width + x) * 4 + channel] - reference[(ry * referenceWidth + rx) * 4 + channel]);
                samples++;
            }
        }
        if (samples == 0) throw new InvalidOperationException("No alignment pixels were sampled.");
        return error / samples;
    }

    private static async Task SnapshotAsync(object core, object window, object vm, string name, bool alignment = false)
    {
        await Task.Delay(1200);
        var state = await Script(core, "JSON.stringify({source:document.getElementById('terminalBackground').src,loaded:document.getElementById('terminalBackground').naturalWidth,visible:document.documentElement.dataset.terminalBackground,opacity:getComputedStyle(document.getElementById('terminalBackground')).opacity,theme:terminal.options.theme.background,text:terminal.buffer.active.getLine(0).translateToString(true),imageBounds:document.getElementById('terminalBackground').getBoundingClientRect().toJSON()})");
        var screenshot = await ((IAsyncOperation<string>)core.GetType().GetMethod("CallDevToolsProtocolMethodAsync")!.Invoke(core, ["Page.captureScreenshot", "{\"format\":\"png\"}"])!).AsTask();
        var bytes = Convert.FromBase64String(JsonDocument.Parse(screenshot).RootElement.GetProperty("data").GetString()!);
        await File.WriteAllBytesAsync(Output + "." + name + ".png", bytes);
        double? alignmentError = alignment ? await MeasureAlignmentAsync(window, bytes) : null;
        var picker = Field(window, "BackgroundAnchorPicker");
        var position = Convert.ToInt32(vm.GetType().GetProperty("BackgroundPosition")!.GetValue(vm));
        var enabled = (bool)picker.GetType().GetProperty("IsEnabled")!.GetValue(picker)!;
        if (enabled != (bool)vm.GetType().GetProperty("CanEditBackgroundPresets")!.GetValue(vm)!)
            throw new InvalidOperationException("Position picker enabled state did not follow preset editing.");
        var grid = picker.GetType().GetProperty("Content")!.GetValue(picker)!;
        var children = (System.Collections.IEnumerable)grid.GetType().GetProperty("Children")!.GetValue(grid)!;
        var buttons = children.Cast<object>().ToArray();
        if (buttons.Length != 5 || (double)picker.GetType().GetProperty("ActualHeight")!.GetValue(picker)! > 28)
            throw new InvalidOperationException("The position picker must contain five buttons in a single row.");
        var selected = buttons.Where(child => (bool?)child.GetType().GetProperty("IsChecked")!.GetValue(child) == true).ToArray();
        if (selected.Length != 1 || selected[0].GetType().GetProperty("CommandParameter")!.GetValue(selected[0])!.ToString() != position.ToString())
            throw new InvalidOperationException($"Position radio buttons did not follow the selected preset: expected {position}, checked {selected.Length}, values {string.Join(',', selected.Select(child => child.GetType().GetProperty("CommandParameter")!.GetValue(child)))}.");
        await File.WriteAllTextAsync(Output + "." + name + ".json", JsonSerializer.Serialize(new {
            state = JsonSerializer.Deserialize<string>(state),
            path = window.GetType().GetField("_lastAppliedCuteBackgroundPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window),
            error = vm.GetType().GetProperty("LastXtermLayoutError")!.GetValue(vm),
            alignmentError, position, positionPickerEnabled = enabled
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
            // Bindings in unselected tab content are not active until loaded.
            var tabs = Field(window, "InspectorTabView");
            tabs.GetType().GetProperty("SelectedIndex")!.SetValue(tabs, 7);
            await Task.Delay(250);
            vm.GetType().GetProperty("CuteBackgroundMode")!.SetValue(vm, true);
            vm.GetType().GetProperty("CuteBackgroundImagePath")!.SetValue(vm, "");
            void SetPosition(int value)
            {
                var picker = Field(window, "BackgroundAnchorPicker");
                if ((bool)picker.GetType().GetProperty("IsEnabled")!.GetValue(picker)!)
                {
                    var grid = picker.GetType().GetProperty("Content")!.GetValue(picker)!;
                    var children = (System.Collections.IEnumerable)grid.GetType().GetProperty("Children")!.GetValue(grid)!;
                    var button = children.Cast<object>().Single(child => child.GetType().GetProperty("CommandParameter")!.GetValue(child)!.ToString() == value.ToString());
                    // Exercise the real IsChecked -> view-model binding, as
                    // pointer and keyboard selection do, without OS input.
                    button.GetType().GetProperty("IsChecked")!.SetValue(button, true);
                    if (Convert.ToInt32(vm.GetType().GetProperty("BackgroundPosition")!.GetValue(vm)) != value)
                        throw new InvalidOperationException("Position selection did not reach the view model.");
                }
                else
                {
                    var command = vm.GetType().GetProperty("SelectBackgroundAnchorCommand")!.GetValue(vm)!;
                    command.GetType().GetMethod("Execute")!.Invoke(command, [value.ToString()]);
                }
            }
            SetPosition(4);
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
            vm.GetType().GetProperty("CuteBackgroundMode")!.SetValue(vm, true);
            vm.GetType().GetProperty("CuteBackgroundOpacity")!.SetValue(vm, 1.0);
            vm.GetType().GetProperty("XtermBackgroundTransparencyPercent")!.SetValue(vm, 0);
            await SnapshotAsync(core, window, vm, "alignment-initial", alignment: true);
            var appWindow = window.GetType().GetProperty("AppWindow")!.GetValue(window)!;
            appWindow.GetType().GetMethod("Resize")!.Invoke(appWindow, [new SizeInt32(1000, 820)]);
            await SnapshotAsync(core, window, vm, "alignment-narrow", alignment: true);
            appWindow.GetType().GetMethod("Resize")!.Invoke(appWindow, [new SizeInt32(1460, 900)]);
            await SnapshotAsync(core, window, vm, "alignment-wide", alignment: true);
            window.GetType().GetMethod("ToggleInspectorCollapsed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            await SnapshotAsync(core, window, vm, "alignment-collapsed", alignment: true);
            window.GetType().GetMethod("ToggleInspectorCollapsed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var reloads = vm.GetType().GetProperty("CuteBackgroundImageReloadCount")!;
            var beforeReloads = (long)reloads.GetValue(vm)!;
            await Script(core, "window.nativeBackgroundReference = document.getElementById('terminalBackground'); true");
            foreach (var (shape, size) in new[] { ("portrait", new SizeInt32(1000, 1200)), ("wide", new SizeInt32(1700, 800)) })
            {
                appWindow.GetType().GetMethod("Resize")!.Invoke(appWindow, [size]);
                foreach (var position in new[] { 3, 1, 4, 7, 5 })
                {
                    SetPosition(position);
                    await SnapshotAsync(core, window, vm, $"alignment-cover-{shape}-{position}", alignment: true);
                }
            }
            if ((long)reloads.GetValue(vm)! != beforeReloads || await Script(core,
                "window.nativeBackgroundReference === document.getElementById('terminalBackground')") != "true")
                throw new InvalidOperationException("Position or resize reloaded the background image.");
            var positionPicker = Field(window, "BackgroundAnchorPicker");
            positionPicker.GetType().GetMethod("StartBringIntoView", Type.EmptyTypes)!.Invoke(positionPicker, null);
            await Task.Delay(500);
            await CaptureElementAsync(Field(window, "SettingsFormScrollViewer"), Output + ".settings.png");
            await File.WriteAllTextAsync(Output, "Completed without serial connection or profile save.");
            Environment.Exit(0);
        }
        catch (Exception error) { await File.WriteAllTextAsync(Output, error.ToString()); Environment.Exit(2); }
    }
}
