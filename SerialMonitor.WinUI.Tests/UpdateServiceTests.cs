using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class UpdateServiceTests
{
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
