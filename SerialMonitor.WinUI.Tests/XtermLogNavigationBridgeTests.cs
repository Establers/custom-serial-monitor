using System.Text.Json;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class XtermLogNavigationBridgeTests
{
    [Theory]
    [InlineData("1.793149200123E+12", 1793149200123L)]
    [InlineData("1793149200123.0", 1793149200123L)]
    [InlineData("179314920012300e-2", 1793149200123L)]
    [InlineData("-1.0", -1L)]
    [InlineData("-6.21355968e13", -62135596800000L)]
    [InlineData("2.53402300799999e14", 253402300799999L)]
    [InlineData("0.000e5", 0L)]
    [InlineData("-0.0", 0L)]
    [InlineData("0.0001e4", 1L)]
    public async Task IntegralWebViewNumberNotationDecodesExactlyAcrossAllTimestampFields(string number, long expected)
    {
        var bridge = new XtermLogNavigationBridge((_, _) => Task.FromResult($$"""
            {"startTime":{{number}},"endTime":{{number}},"currentTimestamp":{{number}},
             "histogramStartTime":{{number}},"histogramEndTime":{{number}},
             "viewportStartTime":{{number}},"viewportEndTime":{{number}},"timestamp":{{number}},"ok":true}
            """));

        var snapshot = await bridge.GetSnapshotAsync(0);
        var preview = await bridge.ResolveAsync("70%");

        Assert.NotNull(snapshot);
        Assert.Equal(expected, snapshot.StartTime);
        Assert.Equal(expected, snapshot.EndTime);
        Assert.Equal(expected, snapshot.CurrentTimestamp);
        Assert.Equal(expected, snapshot.HistogramStartTime);
        Assert.Equal(expected, snapshot.HistogramEndTime);
        Assert.Equal(expected, snapshot.ViewportStartTime);
        Assert.Equal(expected, snapshot.ViewportEndTime);
        Assert.Equal(expected, preview!.Timestamp);
    }

    [Theory]
    [InlineData("1793149200123.5")]
    [InlineData("1.7931492001235e12")]
    [InlineData("1793149200123.000000000000000000000000000001")]
    [InlineData("1e-1000")]
    [InlineData("1e1000")]
    [InlineData("1e2147483648")]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    [InlineData("253402300800000")]
    [InlineData("-62135596800001")]
    [InlineData("\"1793149200123\"")]
    [InlineData("true")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task InvalidTimestampIsRejectedWithoutRoundingAndRawResponseIsDiagnosed(string number)
    {
        var diagnostics = new List<(string Source, Exception Error)>();
        var response = $"{{\"endTime\":{number}}}";
        var bridge = new XtermLogNavigationBridge((_, _) => Task.FromResult(response),
            (source, error) => diagnostics.Add((source, error)));

        await Assert.ThrowsAsync<JsonException>(() => bridge.GetSnapshotAsync(0));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("serialMonitorGetNavigationSnapshot", diagnostic.Source);
        Assert.Contains(JsonSerializer.Serialize(response), diagnostic.Error.Message);
        Assert.IsType<JsonException>(diagnostic.Error.InnerException);
    }

    [Fact]
    public async Task NullableTimestampsStayNullAndFailureDiagnosticsAreBounded()
    {
        var nullBridge = new XtermLogNavigationBridge((_, _) => Task.FromResult("{\"endTime\":null,\"timestamp\":null}"));
        Assert.Null((await nullBridge.GetSnapshotAsync(0))!.EndTime);
        Assert.Null((await nullBridge.ResolveAsync("#1"))!.Timestamp);
        var response = "{\"endTime\":\"" + new string('x', 20_000) + "END-SENTINEL\"}";
        Exception? diagnostic = null;
        var malformed = new XtermLogNavigationBridge((_, _) => Task.FromResult(response), (_, error) => diagnostic = error);

        await Assert.ThrowsAsync<JsonException>(() => malformed.GetSnapshotAsync(0));

        Assert.NotNull(diagnostic);
        Assert.Contains($"length={response.Length}", diagnostic.Message);
        Assert.True(diagnostic.Message.Length < 5000);
        Assert.DoesNotContain("END-SENTINEL", diagnostic.Message);
    }

    [Fact]
    public async Task SnapshotPreservesTimeBoundsFractionalRatesAndCamelCaseResponse()
    {
        string? script = null;
        var bridge = new XtermLogNavigationBridge((value, _) =>
        {
            script = value;
            return Task.FromResult("""
                {"count":1000000,"startTime":1793149200000,"endTime":1793149202000,"endId":"9007199254740993",
                 "currentIndex":499999,"canGoBack":true,"busy":false,"ordered":true,
                 "bins":[0,14,35],"peak":35,"binMilliseconds":3.125,
                 "histogramStartTime":1793149200500,"histogramEndTime":1793149201500,
                 "viewportStartTime":1793149200900,"viewportEndTime":1793149200950}
                """);
        });

        var snapshot = await bridge.GetSnapshotAsync(600, 1793149200500, 1793149201500);

        using var argument = JsonDocument.Parse(Argument(script!, "serialMonitorGetNavigationSnapshot"));
        Assert.Equal(600, argument.RootElement.GetProperty("binCount").GetInt32());
        Assert.Equal(1793149200500, argument.RootElement.GetProperty("startTime").GetInt64());
        Assert.Equal(1793149201500, argument.RootElement.GetProperty("endTime").GetInt64());
        Assert.NotNull(snapshot);
        Assert.Equal(1000000, snapshot.Count);
        Assert.Equal("9007199254740993", snapshot.EndId);
        Assert.Equal(499999, snapshot.CurrentIndex);
        Assert.True(snapshot.CanGoBack);
        Assert.Equal(new double[] { 0, 14, 35 }, snapshot.Bins);
        Assert.Equal(3.125, snapshot.BinMilliseconds);
        Assert.Equal(1793149200900, snapshot.ViewportStartTime);
        Assert.Equal(1793149200950, snapshot.ViewportEndTime);
    }

    [Fact]
    public async Task PreviewEscapesInputAsOneJsonArgumentAndUsesReadOnlyRoute()
    {
        const string input = "14:30');window.serialMonitorClear();//\n\"\\한글\u2028";
        string? script = null;
        var bridge = new XtermLogNavigationBridge((value, _) =>
        {
            script = value;
            return Task.FromResult("""
                {"ok":true,"index":25,"timestamp":1793149200123,"id":"9007199254740993"}
                """);
        });

        var preview = await bridge.ResolveAsync(input);

        using var argument = JsonDocument.Parse(Argument(script!, "serialMonitorResolveNavigation"));
        Assert.Equal(input, argument.RootElement.GetString());
        Assert.DoesNotContain("window.serialMonitorNavigate(", script!);
        Assert.DoesNotContain("\n", script!);
        Assert.NotNull(preview);
        Assert.True(preview.Ok);
        Assert.Equal("9007199254740993", preview.Id);
        Assert.Equal(1793149200123, preview.Timestamp);
    }

    [Fact]
    public async Task MutationRoutesAreDistinctAndBackHasNoSyntheticArgument()
    {
        var scripts = new List<string>();
        var bridge = new XtermLogNavigationBridge((script, _) =>
        {
            scripts.Add(script);
            return Task.FromResult("{\"ok\":false,\"error\":\"expired\"}");
        });

        var navigate = await bridge.NavigateAsync("#500000");
        await bridge.NavigateToTimeAsync(1793149200123);
        await bridge.NavigateToIdAsync("9007199254740993");
        var back = await bridge.BackAsync();

        using var destination = JsonDocument.Parse(Argument(scripts[0], "serialMonitorNavigate"));
        Assert.Equal("#500000", destination.RootElement.GetString());
        Assert.Equal("1793149200123", Argument(scripts[1], "serialMonitorNavigateToTime"));
        using var anchor = JsonDocument.Parse($"[{Argument(scripts[2], "serialMonitorNavigateToIndex")}]");
        Assert.Equal(-1, anchor.RootElement[0].GetInt32());
        Assert.Equal("9007199254740993", anchor.RootElement[1].GetString());
        Assert.Equal(string.Empty, Argument(scripts[3], "serialMonitorNavigationBack"));
        Assert.False(navigate!.Ok);
        Assert.Equal("expired", back!.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    [InlineData("null")]
    public async Task MissingBrowserResponseIsUnavailableInsteadOfASuccess(string? response)
    {
        var bridge = new XtermLogNavigationBridge((_, _) => Task.FromResult(response!));

        Assert.Null(await bridge.GetSnapshotAsync(0));
        Assert.Null(await bridge.ResolveAsync("70%"));
        Assert.Null(await bridge.NavigateAsync("70%"));
    }

    [Fact]
    public async Task MalformedResponseAndExecutorFailureRemainVisibleToTheCaller()
    {
        var malformed = new XtermLogNavigationBridge((_, _) => Task.FromResult("{broken"));
        await Assert.ThrowsAsync<JsonException>(() => malformed.ResolveAsync("70%"));
        var failure = new InvalidOperationException("WebView is closing");
        var unavailable = new XtermLogNavigationBridge((_, _) => Task.FromException<string>(failure));

        var received = await Assert.ThrowsAsync<InvalidOperationException>(() => unavailable.GetSnapshotAsync(600));

        Assert.Same(failure, received);
    }

    [Fact]
    public async Task AlreadyCancelledRequestNeverReachesTheBrowser()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var bridge = new XtermLogNavigationBridge((_, _) =>
        {
            calls++;
            return Task.FromResult("null");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.NavigateAsync("#1", cancellation.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellingAnInFlightRequestReachesTheExecutor()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new XtermLogNavigationBridge(async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "null";
        });
        var pending = bridge.GetSnapshotAsync(600, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LateResponseIsRejectedAfterActivationWasCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var bridge = new XtermLogNavigationBridge((_, _) =>
        {
            // WebView script execution can finish after the native tab closes.
            cancellation.Cancel();
            return Task.FromResult("{\"ok\":true,\"index\":20}");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.ResolveAsync("70%", cancellation.Token));
    }

    private static string Argument(string script, string function)
    {
        var prefix = $"window.{function} ? window.{function}(";
        const string suffix = ") : null;";
        Assert.StartsWith(prefix, script);
        Assert.EndsWith(suffix, script);
        return script[prefix.Length..^suffix.Length];
    }
}
