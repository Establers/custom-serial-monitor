using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class UpdateViewModelTests
{
    [Fact]
    public async Task SkippedRelease_IsHiddenAutomaticallyButManualCheckCanRecoverIt()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.CheckAsync(true);
        Assert.True(model.ShowNotification);
        await model.SkipAsync();
        Assert.False(model.ShowNotification);
        Assert.Equal("1.4.0.0", service.Saved!.SkippedVersion);
        await model.CheckAsync(false);
        Assert.False(model.ShowNotification);
        await model.CheckAsync(true);
        Assert.True(model.ShowNotification);
        service.Tag = "v1.5.0";
        await model.CheckAsync(true);
        Assert.True(model.ShowNotification);
    }

    [Fact]
    public async Task NetworkFailure_IsContainedAndDoesNotShowUpdateBanner()
    {
        var service = new FakeService { Fail = true };
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.CheckAsync(false);
        Assert.False(model.ShowNotification);
        Assert.Equal(UpdateCheckState.Failed, model.State);
        Assert.NotNull(service.Saved!.LastAttemptUtc);
    }

    [Fact]
    public async Task CurrentOrNewerInstalledVersion_DoesNotPrompt()
    {
        using var model = new UpdateViewModel(new FakeService(), new Version(1, 4, 0, 0));
        await model.CheckAsync(true);
        Assert.False(model.ShowNotification);
        Assert.False(model.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosedWindow_DoesNotStartNetworkRequest()
    {
        var service = new FakeService();
        var model = new UpdateViewModel(service);
        model.Dispose();
        await model.CheckAsync(true);
        Assert.Equal(0, service.Checks);
    }

    [Fact]
    public async Task CachedRelease_RemainsVisibleAfterRestartWithoutAnotherRequest()
    {
        var service = new FakeService();
        using (var first = new UpdateViewModel(service, new Version(1, 3, 8)))
            await first.CheckAsync(true);
        using var restarted = new UpdateViewModel(service, new Version(1, 3, 8));
        await restarted.LoadPreferencesAsync();
        Assert.True(restarted.ShowNotification);
        Assert.Equal(UpdateCheckState.Available, restarted.State);
        Assert.Equal(1, service.Checks);
        Assert.NotNull(service.Saved!.LastSuccessUtc);
    }

    [Fact]
    public async Task SkippedRelease_RemainsSuppressedAfterRestart()
    {
        var service = new FakeService();
        using (var first = new UpdateViewModel(service, new Version(1, 3, 8)))
        {
            await first.CheckAsync(true);
            await first.SkipAsync();
        }
        using var restarted = new UpdateViewModel(service, new Version(1, 3, 8));
        await restarted.LoadPreferencesAsync();
        Assert.False(restarted.ShowNotification);
        Assert.True(restarted.IsSkipped);
        Assert.Equal("Visible", restarted.ReleaseVisibility);
    }

    [Fact]
    public async Task FailedRefresh_PreservesLastSuccessfulResultAndTime()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.CheckAsync(true);
        var successTime = service.Saved!.LastSuccessUtc;
        service.Fail = true;
        await model.CheckAsync(true);
        Assert.Equal(UpdateCheckState.Failed, model.State);
        Assert.True(model.ShowNotification);
        Assert.Equal(successTime, service.Saved.LastSuccessUtc);
    }

    [Fact]
    public async Task UpdatingApp_RemovesCachedNotification()
    {
        var service = new FakeService { Saved = new() { LastKnownTag = "v1.4.0" } };
        using var model = new UpdateViewModel(service, new Version(1, 4, 0));
        await model.LoadPreferencesAsync();
        Assert.False(model.ShowNotification);
        Assert.Equal(UpdateCheckState.Current, model.State);
        Assert.Equal("Collapsed", model.ReleaseVisibility);
    }

    [Fact]
    public async Task AnotherWindowDisablingAutomatic_IsRecheckedBeforeNetworkAccess()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.LoadPreferencesAsync();
        await service.ApplyAsync(new(Automatic: false), default);
        await model.CheckAsync(false);
        Assert.Equal(0, service.Checks);
        Assert.False(model.Automatic);
    }

    [Fact]
    public async Task MetadataSave_DoesNotSendStaleUserSettings()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.LoadPreferencesAsync();
        await service.ApplyAsync(new(Automatic: false, SkippedVersion: "1.4.0.0"), default);
        await model.CheckAsync(true);
        Assert.False(service.Saved!.Automatic);
        Assert.Equal("1.4.0.0", service.Saved.SkippedVersion);
        Assert.False(model.Automatic);
        Assert.True(model.IsSkipped);
    }

    [Fact]
    public async Task RapidToggles_CoalesceWritesAndKeepLastChoice()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service);
        await model.LoadPreferencesAsync();
        service.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Automatic = false;
        for (var i = 0; i < 100; i++) model.Automatic = !model.Automatic;
        Assert.False(model.Automatic);
        Assert.Equal(1, service.SaveCalls);
        service.SaveGate.SetResult();
        await WaitUntilAsync(() => service.SaveCalls == 2 && service.ActiveSaves == 0);
        Assert.False(model.Automatic);
        Assert.False(service.Saved!.Automatic);
        Assert.Equal(1, service.MaxActiveSaves);
    }

    [Fact]
    public async Task PendingNetwork_DoesNotBlockCallerOrAllowDuplicateRequests_AndCancelsOnClose()
    {
        var service = new FakeService { CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var model = new UpdateViewModel(service);
        var pending = model.CheckAsync(true);
        Assert.False(pending.IsCompleted);
        Assert.True(model.IsChecking);
        await model.CheckAsync(true);
        Assert.Equal(1, service.Checks);
        model.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(model.ShowNotification);
    }

    [Fact]
    public async Task PendingResultSave_KeepsCheckBusyUntilStorageFinishes()
    {
        var service = new FakeService { CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        var pending = model.CheckAsync(true);
        service.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CheckGate.SetResult();
        await WaitUntilAsync(() => service.ActiveSaves == 1);
        Assert.True(model.IsChecking);
        await model.CheckAsync(true);
        Assert.Equal(1, service.Checks);
        service.SaveGate.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsChecking);
        Assert.True(model.ShowNotification);
    }

    [Fact]
    public async Task OverlappingLocalEdits_DoNotPreventLaterSharedSettingsRefresh()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.LoadPreferencesAsync();
        await model.CheckAsync(true);
        service.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Automatic = false;
        var skip = model.SkipAsync();
        service.SaveGate.SetResult();
        await skip.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => service.ActiveSaves == 0);
        await service.ApplyAsync(new(Automatic: true), default);
        await model.CheckAsync(false);
        Assert.True(model.Automatic);
        Assert.True(model.IsSkipped);
    }

    [Fact]
    public async Task SuccessfulCacheWrite_DoesNotHideUnsavedUserPreferenceError()
    {
        var service = new FakeService { FailPreferenceWrite = true };
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.LoadPreferencesAsync();
        model.Automatic = false;
        await WaitUntilAsync(() => service.ActiveSaves == 0);
        await model.CheckAsync(true);
        Assert.False(model.Automatic);
        Assert.True(service.Saved!.Automatic);
        Assert.NotEmpty(model.PreferenceError);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("v1.3.8", false)]
    [InlineData("v1.3.8", true)]
    public async Task AnotherWindowCompletingCheck_RefreshesCacheWhenDailyCheckIsSkipped(string? previousTag, bool skipped)
    {
        var service = new FakeService
        {
            Saved = new() { LastKnownTag = previousTag, LastSuccessUtc = previousTag is null ? null : DateTimeOffset.UtcNow.AddDays(-2) }
        };
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.LoadPreferencesAsync();
        var previousCheckedText = model.LastCheckedText;
        var now = DateTimeOffset.UtcNow;
        await service.ApplyAsync(new(LastAttemptUtc: now, LastSuccessUtc: now, LastKnownTag: "v1.4.0",
            SkippedVersion: skipped ? "1.4.0.0" : null), default);

        await model.CheckAsync(false);

        Assert.Equal(0, service.Checks);
        Assert.Equal(UpdateCheckState.Available, model.State);
        Assert.Equal(!skipped, model.ShowNotification);
        Assert.Equal(skipped, model.IsSkipped);
        Assert.Contains("v1.4.0", model.NotificationText);
        Assert.NotEqual(previousCheckedText, model.LastCheckedText);
        Assert.True(model.OpenCommand.CanExecute(null));
        Assert.False(model.IsChecking);
    }

    [Fact]
    public async Task AutomaticPreferenceSave_NotifiesBindingsWhenAnotherWindowSkippedCachedRelease()
    {
        var service = new FakeService { Saved = new() { LastKnownTag = "v1.4.0", LastSuccessUtc = DateTimeOffset.UtcNow } };
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.LoadPreferencesAsync();
        var displayedVisibility = model.NotificationVisibility;
        var displayedStatus = model.Status;
        var displayedSkipped = model.IsSkipped;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.NotificationVisibility)) displayedVisibility = model.NotificationVisibility;
            if (args.PropertyName == nameof(model.Status)) displayedStatus = model.Status;
            if (args.PropertyName == nameof(model.IsSkipped)) displayedSkipped = model.IsSkipped;
        };
        await service.ApplyAsync(new(SkippedVersion: "1.4.0.0"), default);

        model.Automatic = false;
        await WaitUntilAsync(() => model.IsSkipped);

        Assert.Equal("Collapsed", displayedVisibility);
        Assert.Equal(model.Status, displayedStatus);
        Assert.True(displayedSkipped);
        Assert.False(model.ShowNotification);
        Assert.Equal(0, service.Checks);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeService : IUpdateService
    {
        public UpdatePreferences? Saved;
        public bool Fail;
        public bool FailPreferenceWrite;
        public string Tag = "v1.4.0";
        public int Checks;
        public TaskCompletionSource? SaveGate;
        public TaskCompletionSource? CheckGate;
        public int SaveCalls;
        public int ActiveSaves;
        public int MaxActiveSaves;
        public Task<UpdatePreferences> LoadAsync(CancellationToken token) => Task.FromResult(Clone(Saved ?? new UpdatePreferences()));
        private static UpdatePreferences Clone(UpdatePreferences source) => new()
        {
            Automatic = source.Automatic, SkippedVersion = source.SkippedVersion,
            LastAttemptUtc = source.LastAttemptUtc, LastKnownTag = source.LastKnownTag, LastSuccessUtc = source.LastSuccessUtc
        };
        public async Task<UpdatePreferences> ApplyAsync(UpdatePreferenceChange change, CancellationToken token)
        {
            SaveCalls++;
            ActiveSaves++;
            MaxActiveSaves = Math.Max(MaxActiveSaves, ActiveSaves);
            try
            {
                if (SaveGate is not null) await SaveGate.Task.WaitAsync(token);
                if (FailPreferenceWrite && change.Automatic is not null) throw new IOException("Preference write failed");
                Saved ??= new();
                change.ApplyTo(Saved);
                return Clone(Saved);
            }
            finally { ActiveSaves--; }
        }
        public async Task<AppRelease> CheckAsync(CancellationToken token)
        {
            Checks++;
            if (CheckGate is not null) await CheckGate.Task.WaitAsync(token);
            if (Fail) throw new HttpRequestException("Offline");
            return new AppRelease(Tag, UpdateService.ParseVersion(Tag)!, new Uri("https://github.com/Establers/custom-serial-monitor/releases/tag/" + Tag));
        }
    }
}
