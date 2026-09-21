using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class UpdateServiceTests
{
    private const string StableManifest = """{"tag_name":"v1.4.0","draft":false,"prerelease":false}""";

    [Fact]
    public async Task SuccessfulApi_DoesNotContactManifest()
    {
        var handler = new RoutingHandler((_, _) => Task.FromResult(Response(200, StableManifest)));
        using var client = new System.Net.Http.HttpClient(handler);
        var result = await new UpdateService("unused.json", client).CheckAsync(default);
        Assert.Equal("v1.4.0", result.Tag);
        Assert.Single(handler.Requests);
        Assert.Equal("api.github.com", handler.Requests[0].Host);
    }

    [Theory]
    [InlineData(429, "", false)]
    [InlineData(403, "", true)]
    [InlineData(403, "{\"message\":\"API rate limit exceeded for shared IP.\"}", false)]
    [InlineData(403, "{\"message\":\"You have exceeded a secondary rate limit.\"}", false)]
    public async Task RateLimitedApi_UsesManifestAndRespectsCooldown(int status, string body, bool exhaustedHeader)
    {
        var handler = new RoutingHandler((request, _) =>
        {
            if (request.RequestUri!.Host != "api.github.com") return Task.FromResult(Response(200, StableManifest));
            var response = Response(status, body);
            if (exhaustedHeader) response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
            return Task.FromResult(response);
        });
        using var client = new System.Net.Http.HttpClient(handler);
        var service = new UpdateService("unused.json", client);
        Assert.Equal("v1.4.0", (await service.CheckAsync(default)).Tag);
        Assert.Equal("v1.4.0", (await service.CheckAsync(default)).Tag);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(UpdateService.ManifestUrl, handler.Requests[1].AbsoluteUri);
        Assert.Equal(UpdateService.ManifestUrl, handler.Requests[2].AbsoluteUri);
    }

    [Theory]
    [InlineData(403, "{\"message\":\"Forbidden\"}")]
    [InlineData(403, "<html>Access denied</html>")]
    [InlineData(404, "")]
    [InlineData(500, "")]
    public async Task OtherHttpErrors_DoNotTriggerFallback(int status, string body)
    {
        var handler = new RoutingHandler((_, _) => Task.FromResult(Response(status, body)));
        using var client = new System.Net.Http.HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateService("unused.json", client).CheckAsync(default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(404, "")]
    [InlineData(200, "<html>Unavailable</html>")]
    [InlineData(200, "{\"tag_name\":\"v1.5.0-beta\",\"draft\":false,\"prerelease\":true}")]
    public async Task InvalidFallback_FailsInsteadOfReportingCurrent(int status, string body)
    {
        var handler = new RoutingHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com" ? Response(429, "") : Response(status, body)));
        using var client = new System.Net.Http.HttpClient(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new UpdateService("unused.json", client).CheckAsync(default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingFallback_SupportsShutdownAndHttpTimeout(bool shutdown)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RoutingHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Response(429, "");
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Response(200, StableManifest);
        });
        using var client = new System.Net.Http.HttpClient(handler)
        {
            Timeout = shutdown ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(250)
        };
        using var cancellation = new CancellationTokenSource();
        var pending = new UpdateService("unused.json", client).CheckAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(pending.IsCompleted);
        if (shutdown) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static System.Net.Http.HttpResponseMessage Response(int status, string body) =>
        new((System.Net.HttpStatusCode)status) { Content = new System.Net.Http.StringContent(body) };

    private sealed class RoutingHandler(Func<System.Net.Http.HttpRequestMessage, CancellationToken,
        Task<System.Net.Http.HttpResponseMessage>> send) : System.Net.Http.HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return send(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData("v1.3.10", "1.3.9", true)]
    [InlineData("v1.3.8", "1.3.8.0", false)]
    [InlineData("v1.2.0", "1.3.8", false)]
    [InlineData("v2.0.0", "1.99.0", true)]
    public void VersionComparison_IsNumericAndNormalizesRevision(string latest, string installed, bool newer)
    {
        Assert.Equal(newer, UpdateService.ParseVersion(latest)! > UpdateService.ParseVersion(installed)!);
    }

    [Theory]
    [InlineData("v1.4.0-beta")]
    [InlineData("latest")]
    [InlineData("1.4")]
    public void UnsupportedVersions_AreNotReportedAsUpdates(string tag) => Assert.Null(UpdateService.ParseVersion(tag));

    [Fact]
    public void ReleasePage_IsRestrictedToOurRepository()
    {
        var release = UpdateService.ParseRelease("""
            {"tag_name":"v1.4.0","draft":false,"prerelease":false,"html_url":"https://example.com/untrusted"}
            """);
        Assert.Equal("https://github.com/Establers/custom-serial-monitor/releases/tag/v1.4.0", release.Page.AbsoluteUri);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void NonStableReleases_AreRejected(bool draft, bool prerelease)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { tag_name = "v1.4.0", draft, prerelease });
        Assert.Throws<InvalidDataException>(() => UpdateService.ParseRelease(json));
    }

    [Fact]
    public void AutomaticChecks_RespectOptOutAndDailyBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        var preferences = new UpdatePreferences { LastAttemptUtc = now.AddHours(-23) };
        Assert.False(UpdateService.IsAutomaticCheckDue(preferences, now));
        preferences.LastAttemptUtc = now.AddDays(-1);
        Assert.True(UpdateService.IsAutomaticCheckDue(preferences, now));
        preferences.Automatic = false;
        Assert.False(UpdateService.IsAutomaticCheckDue(preferences, now));
        preferences.LastAttemptUtc = null;
        Assert.False(UpdateService.IsAutomaticCheckDue(preferences, now));
    }

    [Fact]
    public async Task UnresponsiveHttpEndpoint_TimesOutWithoutBlockingCaller()
    {
        using var client = new System.Net.Http.HttpClient(new DelayedHandler()) { Timeout = TimeSpan.FromMilliseconds(150) };
        var service = new UpdateService(Path.Combine(Path.GetTempPath(), "unused-updates.json"), client);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var pending = service.CheckAsync(default);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task HttpRequest_IsCancelledByShutdown()
    {
        using var client = new System.Net.Http.HttpClient(new DelayedHandler());
        using var cancellation = new CancellationTokenSource();
        var service = new UpdateService(Path.Combine(Path.GetTempPath(), "unused-updates.json"), client);
        var pending = service.CheckAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class DelayedHandler : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    [Fact]
    public async Task Preferences_SurviveRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialMonitorUpdates-" + Guid.NewGuid());
        var path = Path.Combine(directory, "updates.json");
        try
        {
            var service = new UpdateService(path);
            Assert.True((await service.LoadAsync(default)).Automatic);
            var now = DateTimeOffset.UtcNow;
            await service.ApplyAsync(new(Automatic: false, LastAttemptUtc: now, SkippedVersion: "1.4.0.0"), default);
            var restored = await new UpdateService(path).LoadAsync(default);
            Assert.False(restored.Automatic);
            Assert.Equal(now, restored.LastAttemptUtc);
            Assert.Equal("1.4.0.0", restored.SkippedVersion);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
