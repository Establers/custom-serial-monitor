using System.Diagnostics;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProfileServiceConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SerialMonitorProfileConcurrency", Guid.NewGuid().ToString("N"));
    private string ProfilePath => Path.Combine(_root, "profile.json");
    private static CommandSequence Sequence(string name, string command) => new()
    {
        Name = name, Steps = [new CommandSequenceStep { CommandText = command, CaptureMs = 123,
            TxMode = TxSendMode.Hex, Expect = new("hex", "AB CD") }]
    };

    [Fact]
    public async Task OtherWindowRepeatedStaleSavesAndShutdownCannotLoseRegisteredSequence()
    {
        var ai = new ProfileService();
        var otherWindow = new ProfileService();
        var registered = await ai.LoadAsync(ProfilePath, default);
        var stale = await otherWindow.LoadAsync(ProfilePath, default);
        registered.CommandSequences.Add(Sequence("CLI packet", " AB CD "));
        await ai.SaveAsync(ProfilePath, registered, default);

        for (var i = 0; i < 3; i++)
        {
            stale.UiSettings.LastSearchText = "window " + i;
            await otherWindow.SaveAsync(ProfilePath, stale, default);
            var saved = await new ProfileService().LoadAsync(ProfilePath, default);
            Assert.Equal("CLI packet", Assert.Single(saved.CommandSequences).Name);
            Assert.Equal(" AB CD ", saved.CommandSequences[0].Steps[0].CommandText);
            Assert.Equal(new AutomationResponseExpectation("hex", "AB CD"), saved.CommandSequences[0].Steps[0].Expect);
            Assert.Equal("window " + i, saved.UiSettings.LastSearchText);
        }
        Assert.Empty(stale.CommandSequences); // Saving does not mutate the other GUI's list.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNoOpSequenceSaveCannotReportSuccessWhenTheDiskChanged(bool remoteDelete)
    {
        await Seed(Sequence("shared", "original"));
        var cliWindow = new ProfileService();
        var otherWindow = new ProfileService();
        var submitted = await cliWindow.LoadAsync(ProfilePath, default);
        var remote = await otherWindow.LoadAsync(ProfilePath, default);
        if (remoteDelete) remote.CommandSequences.Clear();
        else remote.CommandSequences[0].Steps[0].CommandText = "newer remote command";
        await otherWindow.SaveAsync(ProfilePath, remote, default);
        var committed = await File.ReadAllBytesAsync(ProfilePath);

        var error = await Assert.ThrowsAsync<ProfileConflictException>(() =>
            cliWindow.SaveSequenceChangeAsync(ProfilePath, submitted, "shared", default));
        Assert.Contains("Reload", error.Message);
        Assert.Equal(committed, await File.ReadAllBytesAsync(ProfilePath));
        Assert.Equal("original", submitted.CommandSequences[0].Steps[0].CommandText);
        Assert.Equal(0, cliWindow.SaveCount);
    }

    [Fact]
    public async Task ExplicitNoOpSequenceSaveCommitsWhenTheRequestedNameIsUnchangedAndPreservesRemoteAdditions()
    {
        await Seed(Sequence("shared", "original"));
        var cliWindow = new ProfileService();
        var otherWindow = new ProfileService();
        var submitted = await cliWindow.LoadAsync(ProfilePath, default);
        var remote = await otherWindow.LoadAsync(ProfilePath, default);
        remote.CommandSequences.Add(Sequence("remote", "added"));
        await otherWindow.SaveAsync(ProfilePath, remote, default);
        await cliWindow.SaveSequenceChangeAsync(ProfilePath, submitted, "shared", default);
        var persisted = await new ProfileService().LoadAsync(ProfilePath, default);
        Assert.Equal(new[] { "shared", "remote" }, persisted.CommandSequences.Select(sequence => sequence.Name));
        Assert.Equal("original", persisted.CommandSequences[0].Steps[0].CommandText);
        Assert.Equal(1, cliWindow.SaveCount);
    }

    [Fact]
    public async Task RemoteDeletionSurvivesRepeatedUnchangedSavesWithoutResurrectionOrDanglingRuleReferences()
    {
        await Seed(Sequence("Recover", "reset"));
        var deleting = new ProfileService();
        var other = new ProfileService();
        var edited = await deleting.LoadAsync(ProfilePath, default);
        var stale = await other.LoadAsync(ProfilePath, default);
        stale.LogRules[0].TriggerSequenceName = "Recover";
        edited.CommandSequences.Clear();
        await deleting.SaveAsync(ProfilePath, edited, default);
        for (var i = 0; i < 3; i++) await other.SaveAsync(ProfilePath, stale, default);

        var disk = await new ProfileService().LoadAsync(ProfilePath, default);
        Assert.Empty(disk.CommandSequences);
        Assert.Null(disk.LogRules[0].TriggerSequenceName);
        Assert.DoesNotContain(disk.EventRules, rule => rule.TriggerSequenceName == "Recover");
        Assert.Single(stale.CommandSequences);
    }

    [Fact]
    public async Task GuiEditRenameAndDeleteStillCommitWhileRemoteAdditionsAreRetained()
    {
        await Seed(Sequence("original", "first"));
        var gui = new ProfileService();
        var ai = new ProfileService();
        var local = await gui.LoadAsync(ProfilePath, default);
        var remote = await ai.LoadAsync(ProfilePath, default);
        remote.CommandSequences.Add(Sequence("remote", "second"));
        await ai.SaveAsync(ProfilePath, remote, default);
        local.CommandSequences[0].Name = "renamed";
        local.CommandSequences[0].Steps[0].CommandText = " edited ";
        await gui.SaveAsync(ProfilePath, local, default);
        var disk = await new ProfileService().LoadAsync(ProfilePath, default);
        Assert.Equal(new[] { "renamed", "remote" }, disk.CommandSequences.Select(sequence => sequence.Name));
        Assert.Equal(" edited ", disk.CommandSequences[0].Steps[0].CommandText);
        local.CommandSequences.Clear();
        await gui.SaveAsync(ProfilePath, local, default);
        Assert.Equal("remote", Assert.Single((await new ProfileService().LoadAsync(ProfilePath, default)).CommandSequences).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentEditVersusDeleteConflictsInsteadOfDroppingEditsOrResurrectingDeletion(bool deleteFirst)
    {
        await Seed(Sequence("shared", "original"));
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await first.LoadAsync(ProfilePath, default);
        var b = await second.LoadAsync(ProfilePath, default);
        if (deleteFirst) a.CommandSequences.Clear();
        else a.CommandSequences[0].Steps[0].CommandText = "remote edit";
        await first.SaveAsync(ProfilePath, a, default);
        var committed = await File.ReadAllBytesAsync(ProfilePath);
        if (deleteFirst) b.CommandSequences[0].Steps[0].CommandText = "stale edit";
        else b.CommandSequences.Clear();
        var error = await Assert.ThrowsAsync<ProfileConflictException>(() => second.SaveAsync(ProfilePath, b, default));
        Assert.Contains("Reload", error.Message);
        Assert.Equal(committed, await File.ReadAllBytesAsync(ProfilePath));
        Assert.Equal(1, second.SaveErrorCount);
        Assert.NotNull(second.LastError);
    }

    [Theory]
    [InlineData("tx")]
    [InlineData("capture")]
    [InlineData("expect")]
    [InlineData("delay")]
    [InlineData("repeat")]
    [InlineData("ending")]
    [InlineData("comment")]
    [InlineData("step_name")]
    [InlineData("whitespace")]
    public async Task ConflictingMetadataOnlyEditsUseImmutableBaselineAndAreNotSilentlyOverwritten(string field)
    {
        await Seed(Sequence("shared", "status"));
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await first.LoadAsync(ProfilePath, default);
        var b = await second.LoadAsync(ProfilePath, default);
        var step = a.CommandSequences[0].Steps[0];
        switch (field)
        {
            case "tx": step.TxMode = TxSendMode.Terminal; break;
            case "capture": step.CaptureMs = 456; break;
            case "expect": step.Expect = new("text", "new literal"); break;
            case "delay": step.DelayAfterMs = 456; break;
            case "repeat": a.CommandSequences[0].RepeatCount = 2; break;
            case "ending": step.LineEndingMode = TxLineEndingMode.Cr; break;
            case "comment": step.Comment = "remote"; break;
            case "step_name": step.Name = "remote"; break;
            case "whitespace": step.CommandText = " status "; break;
        }
        await first.SaveAsync(ProfilePath, a, default);
        b.CommandSequences[0].Steps[0].Comment = "local";
        var committed = await File.ReadAllBytesAsync(ProfilePath);
        await Assert.ThrowsAsync<ProfileConflictException>(() => second.SaveAsync(ProfilePath, b, default));
        Assert.Equal(committed, await File.ReadAllBytesAsync(ProfilePath));
    }

    [Fact]
    public async Task ConcurrentCaseInsensitiveNameCollisionFailsUntilExplicitReloadAndReview()
    {
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await first.LoadAsync(ProfilePath, default);
        var b = await second.LoadAsync(ProfilePath, default);
        a.CommandSequences.Add(Sequence("shared", "first"));
        b.CommandSequences.Add(Sequence("SHARED", "second"));
        await first.SaveAsync(ProfilePath, a, default);
        await Assert.ThrowsAsync<ProfileConflictException>(() => second.SaveAsync(ProfilePath, b, default));
        b = await second.LoadAsync(ProfilePath, default);
        b.CommandSequences[0].Steps[0].CommandText = "reviewed replacement";
        await second.SaveAsync(ProfilePath, b, default);
        Assert.Equal("reviewed replacement", (await new ProfileService().LoadAsync(ProfilePath, default)).CommandSequences[0].Steps[0].CommandText);
    }

    [Fact]
    public async Task SeparateServiceConcurrentWritersRetainBothAdditionsAndDoNotShareTemporaryFiles()
    {
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await first.LoadAsync(ProfilePath, default);
        var b = await second.LoadAsync(ProfilePath, default);
        a.CommandSequences.Add(Sequence("first", "a"));
        b.CommandSequences.Add(Sequence("second", "b"));
        await File.WriteAllTextAsync(ProfilePath + ".tmp", "another writer's legacy temp");
        await Task.WhenAll(first.SaveAsync(ProfilePath, a, default), second.SaveAsync(ProfilePath, b, default));
        var disk = await new ProfileService().LoadAsync(ProfilePath, default);
        Assert.Equal(new[] { "first", "second" }, disk.CommandSequences.Select(sequence => sequence.Name).Order());
        Assert.Equal("another writer's legacy temp", await File.ReadAllTextAsync(ProfilePath + ".tmp"));
        Assert.Single(Directory.GetFiles(_root, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_root, "*.bak"));
    }

    [Fact]
    public async Task TwoMissingProfileLoadsSerializeInitializationWithoutFallbackErrors()
    {
        var services = Enumerable.Range(0, 4).Select(_ => new ProfileService()).ToArray();
        var profiles = await Task.WhenAll(services.Select(service => service.LoadAsync(ProfilePath, default)));
        Assert.All(services, service => Assert.Null(service.LastError));
        Assert.All(profiles, profile => Assert.Empty(profile.CommandSequences));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ProfilePath));
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
    }

    [Fact]
    public async Task SaveWithoutLoadAndLoadFallbackCannotTreatUnobservedDiskSequencesAsIntentionalDeletion()
    {
        await Seed(Sequence("initial", "a"));
        var writer = new ProfileService();
        var local = writer.CreateDefaultProfile();
        await writer.SaveAsync(ProfilePath, local, default);
        Assert.Single((await new ProfileService().LoadAsync(ProfilePath, default)).CommandSequences);
        await writer.LoadAsync(ProfilePath, default);
        await File.WriteAllTextAsync(ProfilePath, "{broken");
        local = await writer.LoadAsync(ProfilePath, default);
        Assert.Empty(local.CommandSequences);
        var repaired = writer.CreateDefaultProfile();
        repaired.CommandSequences.Add(Sequence("repaired elsewhere", "b"));
        await File.WriteAllTextAsync(ProfilePath, JsonSerializer.Serialize(repaired));
        await writer.SaveAsync(ProfilePath, local, default);
        Assert.Equal("repaired elsewhere", Assert.Single((await new ProfileService().LoadAsync(ProfilePath, default)).CommandSequences).Name);
    }

    [Fact]
    public async Task LeaseFromAnotherProcessWaitsAsynchronouslyAndHonorsCancellationWithoutChangingProfile()
    {
        await Seed(Sequence("keep", "x"));
        var committed = await File.ReadAllBytesAsync(ProfilePath);
        var ready = Path.Combine(_root, "ready");
        var release = Path.Combine(_root, "release");
        var script = Path.Combine(_root, "hold-profile-lock.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$LeasePath, [string]$ReadyPath, [string]$ReleasePath)
            $lease = [IO.File]::Open($LeasePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try {
                [IO.File]::WriteAllText($ReadyPath, 'ready')
                for ($attempt = 0; $attempt -lt 400 -and !(Test-Path -LiteralPath $ReleasePath); $attempt++) { Start-Sleep -Milliseconds 25 }
            } finally { $lease.Dispose() }
            """);
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-LeasePath", ProfilePath + ".lock", "-ReadyPath", ready, "-ReleasePath", release }) info.ArgumentList.Add(argument);
        using var helper = Process.Start(info)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!File.Exists(ready)) await Task.Delay(20, deadline.Token);
            var service = new ProfileService();
            using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(ProfilePath, service.CreateDefaultProfile(), canceled.Token));
            Assert.Equal(committed, await File.ReadAllBytesAsync(ProfilePath));
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release");
            try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { helper.Kill(); await helper.WaitForExitAsync(); }
        }
        Assert.Equal(0, helper.ExitCode);
        var after = new ProfileService();
        await after.SaveAsync(ProfilePath, after.CreateDefaultProfile(), default);
        Assert.Equal("keep", Assert.Single((await after.LoadAsync(ProfilePath, default)).CommandSequences).Name);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    public async Task SavingFallbackDefaultsCanRepairTheSameObservedBrokenFileAfterBackup(string broken)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(ProfilePath, broken);
        var service = new ProfileService();
        var fallback = await service.LoadAsync(ProfilePath, default);
        Assert.NotNull(service.LastError);
        fallback.CommandSequences.Add(Sequence("repaired", "safe"));
        await service.SaveAsync(ProfilePath, fallback, default);
        Assert.Equal("repaired", Assert.Single((await new ProfileService().LoadAsync(ProfilePath, default)).CommandSequences).Name);
        Assert.NotEmpty(Directory.GetFiles(_root, "*.broken_*"));
    }

    [Fact]
    public async Task ChangedBrokenFileCannotBeSilentlyReplacedUsingAnOlderFailedLoadBaseline()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(ProfilePath, "{old broken");
        var service = new ProfileService();
        var fallback = await service.LoadAsync(ProfilePath, default);
        await File.WriteAllTextAsync(ProfilePath, "{different broken");
        await Assert.ThrowsAsync<ProfileConflictException>(() => service.SaveAsync(ProfilePath, fallback, default));
        Assert.Equal("{different broken", await File.ReadAllTextAsync(ProfilePath));
    }

    [Fact]
    public async Task EvictedComparisonBaselineCannotResurrectASequenceDeletedInAnotherWindow()
    {
        await Seed(Sequence("removed remotely", "a"));
        var staleWindow = new ProfileService();
        var original = await staleWindow.LoadAsync(ProfilePath, default);
        for (var i = 0; i < 8; i++) await staleWindow.LoadAsync(Path.Combine(_root, "other-" + i + ".json"), default);
        var deletingWindow = new ProfileService();
        var latest = await deletingWindow.LoadAsync(ProfilePath, default);
        latest.CommandSequences.Clear();
        await deletingWindow.SaveAsync(ProfilePath, latest, default);
        await Assert.ThrowsAsync<ProfileConflictException>(() => staleWindow.SaveAsync(ProfilePath, original, default));
        Assert.Empty((await new ProfileService().LoadAsync(ProfilePath, default)).CommandSequences);
    }

    private async Task Seed(params CommandSequence[] sequences)
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.CommandSequences = sequences.ToList();
        await service.SaveAsync(ProfilePath, profile, default);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
