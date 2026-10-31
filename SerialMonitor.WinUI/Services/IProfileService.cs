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

    // Explicit automation writes must commit this name exactly or report a
    // conflict. A generic stale profile save may legitimately preserve disk data.
    Task SaveSequenceChangeAsync(string path, AppProfile profile, string sequenceName, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Explicit sequence persistence is not supported by this profile provider.");
}
