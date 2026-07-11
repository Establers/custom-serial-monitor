using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface IProfileService
{
    string DefaultProfilePath { get; }

    string LastStatus { get; }

    string? LastError { get; }

    long LoadErrorCount { get; }

    long SaveErrorCount { get; }

    long ProfileNormalizationCount { get; }

    bool HasLoadedProfile { get; }

    DateTimeOffset? LastLoadTime { get; }

    DateTimeOffset? LastSaveTime { get; }

    long LoadCount { get; }

    long SaveCount { get; }

    int LastSchemaVersion { get; }

    string LastRuleMigrationResult { get; }

    long InvalidRuleColorFallbackCount { get; }

    bool LastCuteBackgroundCustomPathCleared { get; }

    string LastCuteBackgroundCustomPathClearReason { get; }

    AppProfile CreateDefaultProfile();

    Task<AppProfile> LoadAsync(string path, CancellationToken cancellationToken);

    Task SaveAsync(string path, AppProfile profile, CancellationToken cancellationToken);
}
