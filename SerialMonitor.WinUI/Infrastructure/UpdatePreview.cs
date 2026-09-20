using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Infrastructure;

// Debug-only, in-memory fixtures for visual QA. Never read/write the user's
// update preferences or contact GitHub when a preview is requested.
internal static class UpdatePreview
{
    public static UpdateViewModel CreateViewModel()
    {
#if DEBUG
        var argument = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--update-preview=", StringComparison.Ordinal));
        if (argument is not null)
            return new UpdateViewModel(new PreviewService(argument.Split('=', 2)[1]), new Version(1, 3, 8, 0));
#endif
        return new UpdateViewModel();
    }

#if DEBUG
    private sealed class PreviewService(string scenario) : IUpdateService
    {
        public Task<UpdatePreferences> LoadAsync(CancellationToken token) => Task.FromResult(_preferences);
        private readonly UpdatePreferences _preferences = new();
        public Task<UpdatePreferences> ApplyAsync(UpdatePreferenceChange change, CancellationToken token)
        {
            change.ApplyTo(_preferences);
            return Task.FromResult(_preferences);
        }
        public async Task<AppRelease> CheckAsync(CancellationToken token)
        {
            await Task.Delay(scenario == "checking" ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1), token);
            if (scenario == "offline") throw new HttpRequestException("Preview: offline");
            var tag = scenario == "current" ? "v1.3.8" : "v1.4.0";
            return UpdateService.CreateRelease(tag, UpdateService.ParseVersion(tag)!);
        }
    }
#endif
}
