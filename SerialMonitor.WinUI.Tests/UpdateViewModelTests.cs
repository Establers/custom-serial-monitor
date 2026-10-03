using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class UpdateViewModelTests
{
    [Fact]
    public async Task SkippedRelease_IsHiddenUntilManualCheckRecoversIt()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.CheckAsync();
        Assert.True(model.ShowNotification);
        await model.SkipAsync();
        Assert.False(model.ShowNotification);
        Assert.Equal("1.4.0.0", service.Saved!.SkippedVersion);
        await model.CheckAsync();
        Assert.True(model.ShowNotification);
        service.Tag = "v1.5.0";
        await model.CheckAsync();
        Assert.True(model.ShowNotification);
    }

    [Fact]
    public async Task NetworkFailure_IsContainedAndDoesNotShowUpdateBanner()
    {
        var service = new FakeService { Fail = true };
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.CheckAsync();
        Assert.False(model.ShowNotification);
        Assert.Equal(UpdateCheckState.Failed, model.State);
        Assert.Null(service.Saved);
    }

    [Fact]
    public async Task CurrentOrNewerInstalledVersion_DoesNotPrompt()
    {
        using var model = new UpdateViewModel(new FakeService(), new Version(1, 4, 0, 0));
        await model.CheckAsync();
        Assert.False(model.ShowNotification);
        Assert.False(model.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosedWindow_DoesNotStartNetworkRequest()
    {
        var service = new FakeService();
        var model = new UpdateViewModel(service);
        model.Dispose();
        await model.InitializeAsync();
        await model.CheckAsync();
        Assert.Equal(0, service.Checks);
    }

    [Fact]
    public async Task CachedRelease_RemainsVisibleAfterRestartWithoutAnotherRequest()
    {
        var service = new FakeService();
        using (var first = new UpdateViewModel(service, new Version(1, 3, 8)))
            await first.CheckAsync();
        using var restarted = new UpdateViewModel(service, new Version(1, 3, 8));
        await restarted.InitializeAsync();
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
            await first.CheckAsync();
            await first.SkipAsync();
        }
        using var restarted = new UpdateViewModel(service, new Version(1, 3, 8));
        await restarted.InitializeAsync();
        Assert.False(restarted.ShowNotification);
        Assert.True(restarted.IsSkipped);
        Assert.Equal("Visible", restarted.ReleaseVisibility);
    }

    [Fact]
    public async Task FailedRefresh_PreservesLastSuccessfulResultAndTime()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.CheckAsync();
        var successTime = service.Saved!.LastSuccessUtc;
        service.Fail = true;
        await model.CheckAsync();
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
    public async Task Initialize_LoadsPreferencesAndOnlyManualCheckStartsNetworkAccess()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        Assert.False(model.CheckCommand.CanExecute(null));
        await model.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await model.InitializeAsync();
        Assert.Equal(0, service.Checks);
        Assert.Equal(0, service.SaveCalls);
        Assert.True(model.IsReady);
        Assert.True(model.CheckCommand.CanExecute(null));
        await model.CheckAsync();
        await model.CheckAsync();
        Assert.Equal(2, service.Checks);
        Assert.True(model.ShowNotification);
    }

    [Fact]
    public async Task MetadataSave_DoesNotSendStaleUserSettings()
    {
        var service = new FakeService();
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.LoadPreferencesAsync();
        await service.ApplyAsync(new(SkippedVersion: "1.4.0.0"), default);
        await model.CheckAsync();
        Assert.Equal("1.4.0.0", service.Saved!.SkippedVersion);
        Assert.True(model.IsSkipped);
        Assert.True(model.ShowNotification);
    }

    [Fact]
    public async Task PendingNetwork_DoesNotBlockCallerOrAllowDuplicateRequests_AndCancelsOnClose()
    {
        var service = new FakeService { CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var model = new UpdateViewModel(service);
        var pending = model.CheckAsync();
        Assert.False(pending.IsCompleted);
        Assert.True(model.IsChecking);
        await model.CheckAsync();
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
        var pending = model.CheckAsync();
        service.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CheckGate.SetResult();
        await WaitUntilAsync(() => service.ActiveSaves == 1);
        Assert.True(model.IsChecking);
        await model.CheckAsync();
        Assert.Equal(1, service.Checks);
        service.SaveGate.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsChecking);
        Assert.True(model.ShowNotification);
    }

    [Fact]
    public async Task SuccessfulCacheWrite_DoesNotHideUnsavedUserPreferenceError()
    {
        var service = new FakeService { FailPreferenceWrite = true };
        using var model = new UpdateViewModel(service, new Version(1, 3, 8));
        await model.LoadPreferencesAsync();
        await model.CheckAsync();
        await model.SkipAsync();
        await model.CheckAsync();
        Assert.True(model.IsSkipped);
        Assert.Null(service.Saved!.SkippedVersion);
        Assert.NotEmpty(model.PreferenceError);
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
        public Task<UpdatePreferences> LoadAsync(CancellationToken token) => Task.FromResult(Clone(Saved ?? new UpdatePreferences()));
        private static UpdatePreferences Clone(UpdatePreferences source) => new()
        {
            SkippedVersion = source.SkippedVersion,
            LastKnownTag = source.LastKnownTag, LastSuccessUtc = source.LastSuccessUtc
        };
        public async Task<UpdatePreferences> ApplyAsync(UpdatePreferenceChange change, CancellationToken token)
        {
            SaveCalls++;
            ActiveSaves++;
            try
            {
                if (SaveGate is not null) await SaveGate.Task.WaitAsync(token);
                if (FailPreferenceWrite && change.SkippedVersion is not null) throw new IOException("Preference write failed");
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
