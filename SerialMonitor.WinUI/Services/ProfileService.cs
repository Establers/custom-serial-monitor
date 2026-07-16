using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class ProfileService : IProfileService
{
    private const int MinVisibleLogLines = 1_000;
    private const int MaxVisibleLogLines = 500_000;
    private const int MinXtermScrollbackSize = 1_000;
    private const int MaxXtermScrollbackSize = 500_000;
    private const int MinHexGroupTimeoutMs = 1;
    private const int MaxHexGroupTimeoutMs = 5_000;
    private const int MinMockStressLinesPerSecond = 1;
    private const int MaxMockStressLinesPerSecond = 50_000;
    private const int MinMockStressBurstSize = 1;
    private const int MaxMockStressBurstSize = 10_000;
    private const double MinCuteBackgroundOpacity = 0.02;
    private const double MaxCuteBackgroundOpacity = 0.50;
    private const int MaxSequenceDelayAfterMs = 600_000;
    private const int MaxCommandHistoryCount = 100;
    private const int CurrentProfileSchemaVersion = 1;
    private const long MinSizeRotationBytes = 1_048_576;
    private const long MaxSizeRotationBytes = 10L * 1024 * 1024 * 1024;
    private static readonly string[] KnownDevelopmentCuteBackgroundPaths =
    {
        @"C:\Users\pjh\Downloads\s88.png"
    };

    private readonly SemaphoreSlim _ioGate = new(1, 1);

    private static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private long _loadErrorCount;
    private long _saveErrorCount;
    private long _profileNormalizationCount;
    private long _loadCount;
    private long _saveCount;
    private long _invalidRuleColorFallbackCount;
    private DateTimeOffset? _lastLoadTime;
    private DateTimeOffset? _lastSaveTime;
    private bool _hasLoadedProfile;
    private bool _lastCuteBackgroundCustomPathCleared;
    private string _lastCuteBackgroundCustomPathClearReason = "No custom cute background path was cleared.";
    private int _lastSchemaVersion = CurrentProfileSchemaVersion;

    static ProfileService()
    {
        Options.Converters.Add(new JsonStringEnumConverter());
    }

    public string DefaultProfilePath { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SerialMonitor",
            "profiles",
            "default.json");

    public string LastStatus { get; private set; } = "Profile service initialized.";

    public string? LastError { get; private set; }

    public long LoadErrorCount => Interlocked.Read(ref _loadErrorCount);

    public long SaveErrorCount => Interlocked.Read(ref _saveErrorCount);

    public long ProfileNormalizationCount => Interlocked.Read(ref _profileNormalizationCount);

    public bool HasLoadedProfile => _hasLoadedProfile;

    public DateTimeOffset? LastLoadTime => _lastLoadTime;

    public DateTimeOffset? LastSaveTime => _lastSaveTime;

    public long LoadCount => Interlocked.Read(ref _loadCount);

    public long SaveCount => Interlocked.Read(ref _saveCount);

    public int LastSchemaVersion => _lastSchemaVersion;

    public string LastRuleMigrationResult { get; private set; } = "No rule migration has run.";

    public long InvalidRuleColorFallbackCount => Interlocked.Read(ref _invalidRuleColorFallbackCount);

    public bool LastCuteBackgroundCustomPathCleared => _lastCuteBackgroundCustomPathCleared;

    public string LastCuteBackgroundCustomPathClearReason => _lastCuteBackgroundCustomPathClearReason;

    public AppProfile CreateDefaultProfile()
    {
        var serialSettings = new SerialSettings();
        var logSettings = new LogSettings
        {
            SaveDirectory = serialSettings.SaveDirectory
        };

        return new AppProfile
        {
            ProfileSchemaVersion = CurrentProfileSchemaVersion,
            Name = "Default",
            SerialSettings = serialSettings,
            LogSettings = logSettings,
            UiSettings = new UiSettings(),
            EventContextSettings = new EventContextSettings(),
            BridgeSettings = new BridgeSettings(),
            LogRules = new List<LogRule>
            {
                new()
                {
                    Name = "ERROR",
                    Keyword = "ERROR",
                    UseForEvent = true,
                    UseForHighlight = true,
                    UseAsViewFilter = true,
                    ForegroundColor = "Red",
                    Priority = 100,
                    MatchDirection = HighlightMatchDirection.RxOnly
                },
                new()
                {
                    Name = "WARN",
                    Keyword = "WARN",
                    UseForEvent = true,
                    UseForHighlight = true,
                    UseAsViewFilter = true,
                    ForegroundColor = "Yellow",
                    Priority = 50,
                    MatchDirection = HighlightMatchDirection.RxOnly
                },
                new()
                {
                    Name = "FAULT",
                    Keyword = "FAULT",
                    UseForEvent = true,
                    UseForHighlight = true,
                    UseAsViewFilter = true,
                    ForegroundColor = "Magenta",
                    Priority = 100,
                    MatchDirection = HighlightMatchDirection.RxOnly
                }
            },
            EventRules = new List<EventRule>
            {
                new() { Name = "ERROR", Keyword = "ERROR" },
                new() { Name = "WARN", Keyword = "WARN" },
                new() { Name = "FAULT", Keyword = "FAULT" }
            },
            HighlightRules = new List<HighlightRule>
            {
                new()
                {
                    Name = "ERROR",
                    Keyword = "ERROR",
                    UseAsViewFilter = true,
                    ForegroundColor = "Red",
                    Priority = 100,
                    MatchDirection = HighlightMatchDirection.Both
                },
                new()
                {
                    Name = "WARN",
                    Keyword = "WARN",
                    UseAsViewFilter = true,
                    ForegroundColor = "Yellow",
                    Priority = 50,
                    MatchDirection = HighlightMatchDirection.Both
                },
                new()
                {
                    Name = "FAULT",
                    Keyword = "FAULT",
                    UseAsViewFilter = true,
                    ForegroundColor = "Magenta",
                    Priority = 100,
                    MatchDirection = HighlightMatchDirection.Both
                }
            },
            SavedCommands = new List<TxCommand>
            {
                new("status", "status"),
                new("version", "version"),
                new("help", "help")
            },
            CommandHistory = new List<CommandHistoryEntry>(),
            CommandSequences = new List<CommandSequence>()
        };
    }

    public async Task<AppProfile> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(path, cancellationToken);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<AppProfile> LoadCoreAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            var defaultProfile = CreateDefaultProfile();
            try
            {
                await SaveCoreAsync(path, defaultProfile, cancellationToken);
                LastError = null;
                LastStatus = $"Default profile created: {path}";
                RecordLoadSuccess(defaultProfile);
                return defaultProfile;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = $"Default profile could not be created: {ex.Message}. Defaults remain active for this session.";
                LastStatus = "Default profile creation failed; in-memory defaults are active.";
                Interlocked.Increment(ref _loadErrorCount);
                RecordLoadSuccess(defaultProfile);
                return defaultProfile;
            }
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var profile = JsonSerializer.Deserialize<AppProfile>(json, Options);
            var hasConfirmBeforeDisconnect =
                json.IndexOf(nameof(UiSettings.ConfirmBeforeDisconnect), StringComparison.OrdinalIgnoreCase) >= 0;
            var hasShowTimestampInLogView =
                json.IndexOf(nameof(UiSettings.ShowTimestampInLogView), StringComparison.OrdinalIgnoreCase) >= 0;
            var hasLogRules =
                json.IndexOf(nameof(AppProfile.LogRules), StringComparison.OrdinalIgnoreCase) >= 0;
            var hasEventRules =
                json.IndexOf(nameof(AppProfile.EventRules), StringComparison.OrdinalIgnoreCase) >= 0;
            var hasHighlightRules =
                json.IndexOf(nameof(AppProfile.HighlightRules), StringComparison.OrdinalIgnoreCase) >= 0;
            var hasSavedCommands =
                json.IndexOf(nameof(AppProfile.SavedCommands), StringComparison.OrdinalIgnoreCase) >= 0;

            if (profile is not null)
            {
                if (!hasLogRules)
                {
                    profile.LogRules = null!;
                }

                if (!hasEventRules)
                {
                    profile.EventRules = null!;
                }

                if (!hasHighlightRules)
                {
                    profile.HighlightRules = null!;
                }

                if (!hasSavedCommands)
                {
                    profile.SavedCommands = null!;
                }
            }

            var normalized = NormalizeProfile(profile, out var warning);
            if (!hasConfirmBeforeDisconnect)
            {
                normalized.UiSettings.ConfirmBeforeDisconnect = true;
            }

            if (!hasShowTimestampInLogView)
            {
                normalized.UiSettings.ShowTimestampInLogView = true;
            }

            if (!string.IsNullOrWhiteSpace(warning))
            {
                LastError = warning;
                LastStatus = "Profile loaded with defaults for missing or invalid values.";
                Interlocked.Increment(ref _loadErrorCount);
                Interlocked.Increment(ref _profileNormalizationCount);
                RecordLoadSuccess(normalized);
                return normalized;
            }

            LastError = null;
            LastStatus = $"Profile loaded: {path}";
            RecordLoadSuccess(normalized);
            return normalized;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var backupPath = TryBackupBrokenProfile(path);
            LastError = string.IsNullOrWhiteSpace(backupPath)
                ? $"Profile load failed: {ex.Message}. Using defaults."
                : $"Profile load failed: {ex.Message}. Using defaults. Broken profile backup: {backupPath}";
            LastStatus = string.IsNullOrWhiteSpace(backupPath)
                ? "Profile load failed; defaults are active."
                : "Profile load failed; defaults are active and the broken profile was backed up.";
            Interlocked.Increment(ref _loadErrorCount);
            RuntimeDiagnostics.RecordError("ProfileService.LoadAsync", ex);
            return CreateDefaultProfile();
        }
    }

    public async Task SaveAsync(string path, AppProfile profile, CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(path, profile, cancellationToken);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task SaveCoreAsync(string path, AppProfile profile, CancellationToken cancellationToken)
    {
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        try
        {
            var normalized = NormalizeProfile(profile, out _);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            TryDeleteFile(tempPath);
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 32 * 1024,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(path))
            {
                TryDeleteFile(backupPath);
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                TryDeleteFile(backupPath);
            }
            else
            {
                File.Move(tempPath, path);
            }

            LastError = null;
            LastStatus = $"Profile saved: {path}";
            Interlocked.Increment(ref _saveCount);
            _lastSaveTime = DateTimeOffset.Now;
            _lastSchemaVersion = normalized.ProfileSchemaVersion;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempPath);
            LastError = $"Profile save failed: {ex.Message}";
            LastStatus = "Profile save failed.";
            Interlocked.Increment(ref _saveErrorCount);
            RuntimeDiagnostics.RecordError("ProfileService.SaveAsync", ex);
            throw;
        }
    }

    private AppProfile NormalizeProfile(AppProfile? profile, out string? warning)
    {
        var defaults = CreateDefaultProfile();
        var warnings = new List<string>();
        _lastCuteBackgroundCustomPathCleared = false;
        _lastCuteBackgroundCustomPathClearReason = "No custom cute background path was cleared.";

        if (profile is null)
        {
            warning = "Profile JSON was empty. Using defaults.";
            return defaults;
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            profile.Name = defaults.Name;
            warnings.Add("Profile name was missing.");
        }

        if (profile.ProfileSchemaVersion <= 0)
        {
            profile.ProfileSchemaVersion = CurrentProfileSchemaVersion;
        }

        profile.CurrentSessionName = string.IsNullOrWhiteSpace(profile.CurrentSessionName)
            ? string.Empty
            : profile.CurrentSessionName;

        if (profile.SerialSettings is null)
        {
            profile.SerialSettings = defaults.SerialSettings.Clone();
            warnings.Add("Serial settings were missing.");
        }

        NormalizeSerialSettings(profile.SerialSettings, defaults.SerialSettings, warnings);
        if (profile.LastSuccessfulSerialSettings is not null)
        {
            NormalizeSerialSettings(profile.LastSuccessfulSerialSettings, defaults.SerialSettings, warnings);
        }

        if (profile.LogSettings is null)
        {
            profile.LogSettings = defaults.LogSettings.Clone();
            warnings.Add("Log settings were missing.");
        }

        NormalizeLogSettings(profile.LogSettings, profile.SerialSettings, defaults.LogSettings, warnings);
        profile.SerialSettings.SaveDirectory = profile.LogSettings.SaveDirectory;

        if (profile.UiSettings is null)
        {
            profile.UiSettings = defaults.UiSettings.Clone();
            warnings.Add("UI settings were missing.");
        }

        NormalizeUiSettings(profile.UiSettings, defaults.UiSettings, warnings);

        if (profile.EventContextSettings is null)
        {
            profile.EventContextSettings = defaults.EventContextSettings.Clone();
            warnings.Add("Event context settings were missing.");
        }

        NormalizeEventContextSettings(profile.EventContextSettings, warnings);

        if (profile.BridgeSettings is null)
        {
            profile.BridgeSettings = defaults.BridgeSettings.Clone();
            warnings.Add("Bridge settings were missing.");
        }
        else
        {
            profile.BridgeSettings.VirtualPortName = profile.BridgeSettings.VirtualPortName?.Trim() ?? string.Empty;
            if (profile.BridgeSettings.Enabled)
            {
                profile.BridgeSettings.Enabled = false;
                warnings.Add("Bridge start state is never restored from a profile; bridge remains off until explicitly started.");
            }

            profile.BridgeSettings.MaxQueuedChunks = Math.Clamp(
                profile.BridgeSettings.MaxQueuedChunks,
                1,
                65_536);
            profile.BridgeSettings.MaxQueuedBytes = Math.Clamp(
                profile.BridgeSettings.MaxQueuedBytes,
                64 * 1024,
                256 * 1024 * 1024);
            profile.BridgeSettings.ManualTxIdleGuardMs = Math.Clamp(
                profile.BridgeSettings.ManualTxIdleGuardMs,
                0,
                10_000);
            if (string.Equals(
                    profile.BridgeSettings.VirtualPortName,
                    profile.SerialSettings.PortName,
                    StringComparison.OrdinalIgnoreCase))
            {
                profile.BridgeSettings.Enabled = false;
                warnings.Add("Bridge was disabled because its virtual port matched the device port.");
            }
        }

        if (profile.EventRules is null)
        {
            profile.EventRules = defaults.EventRules;
            warnings.Add("Event rules were missing.");
        }
        else
        {
            profile.EventRules.RemoveAll(rule => rule is null);
            NormalizeEventRules(profile.EventRules, warnings);
        }

        if (profile.HighlightRules is null)
        {
            profile.HighlightRules = defaults.HighlightRules;
            warnings.Add("Highlight rules were missing.");
        }
        else
        {
            profile.HighlightRules.RemoveAll(rule => rule is null);
            NormalizeHighlightRules(profile.HighlightRules, warnings);
        }

        if (profile.LogRules is null || profile.LogRules.Count == 0)
        {
            profile.LogRules = MergeLegacyRules(profile.EventRules, profile.HighlightRules);
            if (profile.LogRules.Count == 0)
            {
                profile.LogRules = defaults.LogRules;
                LastRuleMigrationResult = "Unified log rules were missing; default rules were applied.";
                warnings.Add("Unified log rules were missing.");
            }
            else
            {
                LastRuleMigrationResult =
                    $"Migrated {profile.EventRules.Count:N0} event rules and {profile.HighlightRules.Count:N0} highlight rules into {profile.LogRules.Count:N0} unified log rules.";
                warnings.Add("Unified log rules were missing; legacy event/highlight rules were migrated.");
            }
        }
        else
        {
            profile.LogRules.RemoveAll(rule => rule is null);
            NormalizeLogRules(profile.LogRules, warnings);
            LastRuleMigrationResult = $"Loaded {profile.LogRules.Count:N0} unified log rules.";
        }

        NormalizeLogRules(profile.LogRules, warnings);
        profile.EventRules = CreateEventRulesFromLogRules(profile.LogRules);
        profile.HighlightRules = CreateHighlightRulesFromLogRules(profile.LogRules);

        if (profile.SavedCommands is null)
        {
            profile.SavedCommands = defaults.SavedCommands;
            warnings.Add("Saved commands were missing.");
        }
        else
        {
            profile.SavedCommands.RemoveAll(command => command is null || string.IsNullOrWhiteSpace(command.CommandText));
            foreach (var command in profile.SavedCommands)
            {
                command.CommandText = command.CommandText.Trim();
                if (string.IsNullOrWhiteSpace(command.Name))
                {
                    command.Name = command.CommandText;
                    warnings.Add("A saved command name was missing.");
                }
                else
                {
                    command.Name = command.Name.Trim();
                }

                if (!string.IsNullOrWhiteSpace(command.OptionalShortcut))
                {
                    command.OptionalShortcut = command.OptionalShortcut.Trim();
                }
            }
        }

        if (profile.CommandSequences is null)
        {
            profile.CommandSequences = defaults.CommandSequences;
            warnings.Add("Command sequences were missing.");
        }
        else
        {
            NormalizeCommandSequences(profile.CommandSequences, warnings);
        }

        if (profile.CommandHistory is null)
        {
            profile.CommandHistory = defaults.CommandHistory;
        }
        else
        {
            NormalizeCommandHistory(profile.CommandHistory, warnings);
        }

        warning = warnings.Count == 0
            ? null
            : $"Profile had missing or invalid values: {string.Join(" ", warnings)}";
        return profile;
    }

    private static string TryBackupBrokenProfile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.Empty;
            }

            var fileName = Path.GetFileName(path);
            var timestamp = DateTimeOffset.Now.LocalDateTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(directory, $"{fileName}.broken_{timestamp}");
            if (File.Exists(backupPath))
            {
                backupPath = Path.Combine(directory, $"{fileName}.broken_{timestamp}_{DateTimeOffset.Now.Ticks}");
            }

            File.Copy(path, backupPath, overwrite: false);
            return backupPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void RecordLoadSuccess(AppProfile profile)
    {
        _hasLoadedProfile = true;
        _lastLoadTime = DateTimeOffset.Now;
        _lastSchemaVersion = profile.ProfileSchemaVersion;
        Interlocked.Increment(ref _loadCount);
    }

    private static void NormalizeSerialSettings(
        SerialSettings settings,
        SerialSettings defaults,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            settings.PortName = defaults.PortName;
            warnings.Add("Serial port was missing.");
        }

        if (settings.BaudRate is <= 0 or > 10_000_000)
        {
            settings.BaudRate = defaults.BaudRate;
            warnings.Add("Baudrate was invalid.");
        }

        if (settings.DataBits is < 5 or > 8)
        {
            settings.DataBits = defaults.DataBits;
            warnings.Add("Data bits were invalid.");
        }

        if (!Enum.IsDefined(settings.Parity))
        {
            settings.Parity = defaults.Parity;
            warnings.Add("Parity was invalid.");
        }

        if (!Enum.IsDefined(settings.StopBits))
        {
            settings.StopBits = defaults.StopBits;
            warnings.Add("Stop bits were invalid.");
        }

        if (!Enum.IsDefined(settings.Handshake))
        {
            settings.Handshake = defaults.Handshake;
            warnings.Add("Handshake was invalid.");
        }

        if (!Enum.IsDefined(settings.RxLineEnding))
        {
            settings.RxLineEnding = defaults.RxLineEnding;
            warnings.Add("RX line ending was invalid.");
        }

        if (!Enum.IsDefined(settings.TxLineEnding))
        {
            settings.TxLineEnding = defaults.TxLineEnding;
            warnings.Add("TX line ending was invalid.");
        }

        if (!Enum.IsDefined(settings.Encoding))
        {
            settings.Encoding = defaults.Encoding;
            warnings.Add("Encoding mode was invalid.");
        }

        if (string.IsNullOrWhiteSpace(settings.SaveDirectory))
        {
            settings.SaveDirectory = defaults.SaveDirectory;
            warnings.Add("Serial save directory was missing.");
        }
        else if (TryNormalizeDirectory(settings.SaveDirectory, out var normalizedSerialDirectory))
        {
            settings.SaveDirectory = normalizedSerialDirectory;
        }
        else
        {
            settings.SaveDirectory = defaults.SaveDirectory;
            warnings.Add("Serial save directory was invalid.");
        }
    }

    private static void NormalizeLogSettings(
        LogSettings settings,
        SerialSettings serialSettings,
        LogSettings defaults,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(settings.SaveDirectory))
        {
            settings.SaveDirectory = string.IsNullOrWhiteSpace(serialSettings.SaveDirectory)
                ? defaults.SaveDirectory
                : serialSettings.SaveDirectory;
            warnings.Add("Log save directory was missing.");
        }
        else if (TryNormalizeDirectory(settings.SaveDirectory, out var normalizedLogDirectory))
        {
            settings.SaveDirectory = normalizedLogDirectory;
        }
        else
        {
            settings.SaveDirectory = defaults.SaveDirectory;
            warnings.Add("Log save directory was invalid.");
        }

        if (settings.SizeRotationBytes is <= 0)
        {
            settings.SizeRotationBytes = null;
            warnings.Add("Size rotation bytes was invalid.");
        }
        else if (settings.SizeRotationBytes is < MinSizeRotationBytes or > MaxSizeRotationBytes)
        {
            settings.SizeRotationBytes = null;
            warnings.Add("Size rotation bytes was outside the safe range.");
        }
    }

    private void NormalizeUiSettings(
        UiSettings settings,
        UiSettings defaults,
        ICollection<string> warnings)
    {
        if (settings.MaxVisibleLogLines is < MinVisibleLogLines or > MaxVisibleLogLines)
        {
            settings.MaxVisibleLogLines = defaults.MaxVisibleLogLines;
            warnings.Add("Max visible log lines was invalid.");
        }

        if (settings.MaxVisibleEventCount != UiSettings.FixedMaxVisibleEventCount)
        {
            settings.MaxVisibleEventCount = UiSettings.FixedMaxVisibleEventCount;
            warnings.Add("Max visible event count was fixed at 100.");
        }

        if (settings.XtermScrollbackSize is < MinXtermScrollbackSize or > MaxXtermScrollbackSize)
        {
            settings.XtermScrollbackSize = defaults.XtermScrollbackSize;
            warnings.Add("xterm scrollback size was invalid.");
        }

        if (settings.XtermScrollbackSize < settings.MaxVisibleLogLines)
        {
            settings.XtermScrollbackSize = settings.MaxVisibleLogLines;
            warnings.Add("xterm scrollback was raised to match visible log max lines.");
        }

        if (settings.HexGroupTimeoutMs is < MinHexGroupTimeoutMs or > MaxHexGroupTimeoutMs)
        {
            settings.HexGroupTimeoutMs = defaults.HexGroupTimeoutMs;
            warnings.Add("HEX group timeout was invalid.");
        }

        if (!Enum.IsDefined(settings.TimestampDisplayFormat))
        {
            settings.TimestampDisplayFormat = defaults.TimestampDisplayFormat;
            warnings.Add("Timestamp display format was invalid.");
        }

        settings.LastSearchText = settings.LastSearchText?.Trim() ?? string.Empty;
        settings.MarkerText = settings.MarkerText?.Trim() ?? string.Empty;
        settings.CuteBackgroundImagePath = NormalizeCuteBackgroundImagePath(
            settings.CuteBackgroundImagePath,
            defaults.CuteBackgroundImagePath,
            warnings);

        if (!Enum.IsDefined(settings.RxDisplayMode))
        {
            settings.RxDisplayMode = defaults.RxDisplayMode;
            warnings.Add("RX display mode was invalid.");
        }
        else if (settings.RxDisplayMode == RxDisplayMode.Escaped)
        {
            settings.RxDisplayMode = RxDisplayMode.Terminal;
            warnings.Add("Legacy RX Escaped mode was migrated to Terminal.");
        }

        if (!Enum.IsDefined(settings.TxSendMode))
        {
            settings.TxSendMode = defaults.TxSendMode;
            warnings.Add("TX send mode was invalid.");
        }
        else if (settings.TxSendMode == TxSendMode.Escaped)
        {
            settings.TxSendMode = TxSendMode.Terminal;
            warnings.Add("Legacy TX Escaped mode was migrated to Terminal.");
        }

        if (double.IsNaN(settings.CuteBackgroundOpacity) ||
            double.IsInfinity(settings.CuteBackgroundOpacity) ||
            settings.CuteBackgroundOpacity is < MinCuteBackgroundOpacity or > MaxCuteBackgroundOpacity)
        {
            settings.CuteBackgroundOpacity = defaults.CuteBackgroundOpacity;
            warnings.Add("Cute background opacity was invalid.");
        }

        if (settings.MockStressLinesPerSecond is < MinMockStressLinesPerSecond or > MaxMockStressLinesPerSecond)
        {
            settings.MockStressLinesPerSecond = defaults.MockStressLinesPerSecond;
            warnings.Add("Mock stress lines/sec was invalid.");
        }

        if (settings.MockStressBurstSize is < MinMockStressBurstSize or > MaxMockStressBurstSize)
        {
            settings.MockStressBurstSize = defaults.MockStressBurstSize;
            warnings.Add("Mock stress burst size was invalid.");
        }

        if (!Enum.IsDefined(settings.MockGeneratorPattern))
        {
            settings.MockGeneratorPattern = defaults.MockGeneratorPattern;
            warnings.Add("Mock generator pattern was invalid.");
        }
    }

    private string NormalizeCuteBackgroundImagePath(
        string? path,
        string defaultPath,
        ICollection<string> warnings)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.Length > 1024)
        {
            return ClearCuteBackgroundImagePath(
                defaultPath,
                warnings,
                "Custom cute background image path was too long.");
        }

        if (IsKnownDevelopmentCuteBackgroundPath(trimmed))
        {
            return ClearCuteBackgroundImagePath(
                defaultPath,
                warnings,
                "Legacy development cute background image path was cleared.");
        }

        try
        {
            if (!File.Exists(trimmed))
            {
                return ClearCuteBackgroundImagePath(
                    defaultPath,
                    warnings,
                    "Missing custom cute background image path was cleared.");
            }
        }
        catch (Exception ex)
        {
            return ClearCuteBackgroundImagePath(
                defaultPath,
                warnings,
                $"Invalid custom cute background image path was cleared: {ex.Message}");
        }

        return trimmed;
    }

    private string ClearCuteBackgroundImagePath(
        string defaultPath,
        ICollection<string> warnings,
        string reason)
    {
        _lastCuteBackgroundCustomPathCleared = true;
        _lastCuteBackgroundCustomPathClearReason = reason;
        warnings.Add(reason);
        return defaultPath?.Trim() ?? string.Empty;
    }

    private static bool IsKnownDevelopmentCuteBackgroundPath(string path)
    {
        foreach (var knownPath in KnownDevelopmentCuteBackgroundPaths)
        {
            if (PathsEqual(path, knownPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void NormalizeEventContextSettings(
        EventContextSettings settings,
        ICollection<string> warnings)
    {
        if (settings.BeforeContextLines != EventContextSettings.FixedLineCount)
        {
            settings.BeforeContextLines = EventContextSettings.FixedLineCount;
            warnings.Add("Before event context line count was fixed at 5.");
        }

        if (settings.AfterContextLines != EventContextSettings.FixedLineCount)
        {
            settings.AfterContextLines = EventContextSettings.FixedLineCount;
            warnings.Add("After event context line count was fixed at 5.");
        }
    }

    private static bool TryNormalizeDirectory(string directory, out string normalizedDirectory)
    {
        normalizedDirectory = string.Empty;
        try
        {
            normalizedDirectory = Path.GetFullPath(directory.Trim());
            return !string.IsNullOrWhiteSpace(normalizedDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void NormalizeCommandHistory(IList<CommandHistoryEntry> history, ICollection<string> warnings)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var entry = history[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.CommandText))
            {
                history.RemoveAt(i);
                warnings.Add("A command history entry was invalid.");
                continue;
            }

            entry.CommandText = entry.CommandText.Trim();
            if (entry.LastSentTime == default)
            {
                entry.LastSentTime = DateTimeOffset.Now;
            }

            if (entry.Count <= 0)
            {
                entry.Count = 1;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (!seen.Add(history[i].CommandText))
            {
                history.RemoveAt(i);
                warnings.Add("Duplicate command history entries were removed.");
            }
        }

        var ordered = history
            .OrderByDescending(entry => entry.LastSentTime)
            .Take(MaxCommandHistoryCount)
            .ToArray();

        history.Clear();
        foreach (var entry in ordered)
        {
            history.Add(entry);
        }
    }

    private static void NormalizeEventRules(IList<EventRule> rules, ICollection<string> warnings)
    {
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                rule.Name = string.IsNullOrWhiteSpace(rule.Keyword) ? "Event" : rule.Keyword.Trim();
                warnings.Add("An event rule name was missing.");
            }
            else
            {
                rule.Name = rule.Name.Trim();
            }

            rule.Keyword = rule.Keyword?.Trim() ?? string.Empty;

            if (!Enum.IsDefined(rule.MatchDirection))
            {
                rule.MatchDirection = EventMatchDirection.RxOnly;
                warnings.Add("An event match direction was invalid.");
            }

            if (!Enum.IsDefined(rule.Mode))
            {
                rule.Mode = LogRuleMatchMode.Terminal;
                warnings.Add("An event rule match mode was invalid.");
            }

            rule.HighlightColor = string.IsNullOrWhiteSpace(rule.HighlightColor) ? null : rule.HighlightColor.Trim();
            rule.NotificationCooldownSeconds = Math.Clamp(rule.NotificationCooldownSeconds, 5, 3_600);
        }
    }

    private void NormalizeHighlightRules(IList<HighlightRule> rules, ICollection<string> warnings)
    {
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                rule.Name = string.IsNullOrWhiteSpace(rule.Keyword) ? "Highlight" : rule.Keyword;
                warnings.Add("A highlight rule name was missing.");
            }
            else
            {
                rule.Name = rule.Name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(rule.Keyword))
            {
                rule.Keyword = rule.Keyword.Trim();
            }

            rule.ForegroundColor = NormalizeHighlightColorName(rule.ForegroundColor, "highlight rule foreground", warnings);
            rule.BackgroundColor = NormalizeOptionalHighlightColorName(rule.BackgroundColor, "highlight rule background", warnings);

            if (!Enum.IsDefined(rule.MatchDirection))
            {
                rule.MatchDirection = HighlightMatchDirection.Both;
                warnings.Add("A highlight match direction was invalid.");
            }

            if (!Enum.IsDefined(rule.Mode))
            {
                rule.Mode = LogRuleMatchMode.Terminal;
                warnings.Add("A highlight rule match mode was invalid.");
            }
        }
    }

    private void NormalizeLogRules(IList<LogRule> rules, ICollection<string> warnings)
    {
        for (var index = rules.Count - 1; index >= 0; index--)
        {
            var rule = rules[index];
            if (rule is null || string.IsNullOrWhiteSpace(rule.Keyword))
            {
                rules.RemoveAt(index);
                warnings.Add("A log rule was invalid.");
                continue;
            }

            rule.Keyword = rule.Keyword.Trim();
            rule.Name = string.IsNullOrWhiteSpace(rule.Name)
                ? rule.Keyword
                : rule.Name.Trim();
            rule.ForegroundColor = NormalizeHighlightColorName(rule.ForegroundColor, "log rule foreground", warnings);
            rule.BackgroundColor = NormalizeOptionalHighlightColorName(rule.BackgroundColor, "log rule background", warnings);

            if (!Enum.IsDefined(rule.MatchDirection))
            {
                rule.MatchDirection = HighlightMatchDirection.Both;
                warnings.Add("A log rule match direction was invalid.");
            }

            if (!Enum.IsDefined(rule.Mode))
            {
                rule.Mode = LogRuleMatchMode.Terminal;
                warnings.Add("A log rule match mode was invalid.");
            }

            rule.NotificationCooldownSeconds = Math.Clamp(rule.NotificationCooldownSeconds, 5, 3_600);
        }
    }

    private string NormalizeHighlightColorName(
        string? colorName,
        string fieldName,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(colorName))
        {
            return "Default";
        }

        var trimmed = colorName.Trim();
        if (trimmed.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("(none)", StringComparison.OrdinalIgnoreCase))
        {
            return "Default";
        }

        var normalized = NormalizeKnownHighlightColor(trimmed);
        if (normalized is not null)
        {
            return normalized;
        }

        Interlocked.Increment(ref _invalidRuleColorFallbackCount);
        warnings.Add($"An invalid {fieldName} color was reset to Default.");
        return "Default";
    }

    private string? NormalizeOptionalHighlightColorName(
        string? colorName,
        string fieldName,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(colorName) ||
            colorName.Trim().Equals("None", StringComparison.OrdinalIgnoreCase) ||
            colorName.Trim().Equals("(none)", StringComparison.OrdinalIgnoreCase) ||
            colorName.Trim().Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = NormalizeKnownHighlightColor(colorName.Trim());
        if (normalized is not null)
        {
            return normalized;
        }

        Interlocked.Increment(ref _invalidRuleColorFallbackCount);
        warnings.Add($"An invalid {fieldName} color was cleared.");
        return null;
    }

    private static string? NormalizeKnownHighlightColor(string colorName)
    {
        if (colorName.Equals("Grey", StringComparison.OrdinalIgnoreCase))
        {
            return "Gray";
        }

        return colorName.ToLowerInvariant() switch
        {
            "default" => "Default",
            "red" => "Red",
            "orange" => "Orange",
            "yellow" => "Yellow",
            "green" => "Green",
            "cyan" => "Cyan",
            "blue" => "Blue",
            "magenta" => "Magenta",
            "white" => "White",
            "gray" => "Gray",
            _ => null
        };
    }

    private static List<LogRule> MergeLegacyRules(
        IReadOnlyList<EventRule> eventRules,
        IReadOnlyList<HighlightRule> highlightRules)
    {
        var merged = new List<LogRule>();

        foreach (var eventRule in eventRules)
        {
            if (string.IsNullOrWhiteSpace(eventRule.Keyword))
            {
                continue;
            }

            merged.Add(new LogRule
            {
                Name = string.IsNullOrWhiteSpace(eventRule.Name) ? eventRule.Keyword.Trim() : eventRule.Name.Trim(),
                Keyword = eventRule.Keyword.Trim(),
                Enabled = eventRule.Enabled,
                UseForEvent = true,
                UseForHighlight = false,
                UseAsViewFilter = false,
                CaseSensitive = eventRule.CaseSensitive,
                Mode = eventRule.Mode,
                MatchDirection = ConvertDirection(eventRule.MatchDirection),
                ForegroundColor = string.IsNullOrWhiteSpace(eventRule.HighlightColor) ? "Default" : eventRule.HighlightColor.Trim(),
                TrayNotificationEnabled = eventRule.TrayNotificationEnabled,
                SoundNotificationEnabled = eventRule.SoundNotificationEnabled,
                PopupNotificationEnabled = eventRule.PopupNotificationEnabled,
                NotificationCooldownSeconds = eventRule.NotificationCooldownSeconds,
                Priority = 0
            });
        }

        foreach (var highlightRule in highlightRules)
        {
            if (string.IsNullOrWhiteSpace(highlightRule.Keyword))
            {
                continue;
            }

            var match = merged.FirstOrDefault(rule => IsSameRuleIdentity(rule, highlightRule));
            if (match is null)
            {
                merged.Add(new LogRule
                {
                    Name = string.IsNullOrWhiteSpace(highlightRule.Name) ? highlightRule.Keyword.Trim() : highlightRule.Name.Trim(),
                    Keyword = highlightRule.Keyword.Trim(),
                    Enabled = highlightRule.Enabled,
                    UseForEvent = false,
                    UseForHighlight = true,
                    UseAsViewFilter = highlightRule.UseAsViewFilter,
                    CaseSensitive = highlightRule.CaseSensitive,
                    Mode = highlightRule.Mode,
                    MatchDirection = highlightRule.MatchDirection,
                    ForegroundColor = string.IsNullOrWhiteSpace(highlightRule.ForegroundColor) ? "Default" : highlightRule.ForegroundColor.Trim(),
                    BackgroundColor = string.IsNullOrWhiteSpace(highlightRule.BackgroundColor) ? null : highlightRule.BackgroundColor.Trim(),
                    Priority = highlightRule.Priority
                });
                continue;
            }

            match.Enabled = match.Enabled || highlightRule.Enabled;
            match.UseForHighlight = true;
            match.UseAsViewFilter = highlightRule.UseAsViewFilter;
            match.ForegroundColor = string.IsNullOrWhiteSpace(highlightRule.ForegroundColor)
                ? match.ForegroundColor
                : highlightRule.ForegroundColor.Trim();
            match.BackgroundColor = string.IsNullOrWhiteSpace(highlightRule.BackgroundColor)
                ? null
                : highlightRule.BackgroundColor.Trim();
            match.Priority = highlightRule.Priority;
        }

        return merged;
    }

    private static bool IsSameRuleIdentity(LogRule rule, HighlightRule highlightRule)
    {
        return string.Equals(rule.Name?.Trim(), highlightRule.Name?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.Keyword?.Trim(), highlightRule.Keyword?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            rule.CaseSensitive == highlightRule.CaseSensitive &&
            rule.Mode == highlightRule.Mode &&
            rule.Enabled == highlightRule.Enabled;
    }

    private static List<EventRule> CreateEventRulesFromLogRules(IEnumerable<LogRule> rules)
    {
        return rules
            .Where(rule => rule.UseForEvent && !string.IsNullOrWhiteSpace(rule.Keyword))
            .Select(rule => new EventRule
            {
                Name = rule.Name,
                Keyword = rule.Keyword,
                Enabled = rule.Enabled,
                CaseSensitive = rule.CaseSensitive,
                Mode = rule.Mode,
                MatchDirection = ConvertDirection(rule.MatchDirection),
                HighlightColor = rule.ForegroundColor,
                TrayNotificationEnabled = rule.TrayNotificationEnabled,
                SoundNotificationEnabled = rule.SoundNotificationEnabled,
                PopupNotificationEnabled = rule.PopupNotificationEnabled,
                NotificationCooldownSeconds = rule.NotificationCooldownSeconds
            })
            .ToList();
    }

    private static List<HighlightRule> CreateHighlightRulesFromLogRules(IEnumerable<LogRule> rules)
    {
        return rules
            .Where(rule => rule.UseForHighlight && !string.IsNullOrWhiteSpace(rule.Keyword))
            .Select(rule => new HighlightRule
            {
                Name = rule.Name,
                Keyword = rule.Keyword,
                Enabled = rule.Enabled,
                CaseSensitive = rule.CaseSensitive,
                Mode = rule.Mode,
                UseAsViewFilter = rule.UseAsViewFilter,
                ForegroundColor = rule.ForegroundColor,
                BackgroundColor = rule.BackgroundColor,
                Priority = rule.Priority,
                MatchDirection = rule.MatchDirection
            })
            .ToList();
    }

    private static HighlightMatchDirection ConvertDirection(EventMatchDirection direction)
    {
        return direction switch
        {
            EventMatchDirection.TxOnly => HighlightMatchDirection.TxOnly,
            EventMatchDirection.Both => HighlightMatchDirection.Both,
            _ => HighlightMatchDirection.RxOnly
        };
    }

    private static EventMatchDirection ConvertDirection(HighlightMatchDirection direction)
    {
        return direction switch
        {
            HighlightMatchDirection.TxOnly => EventMatchDirection.TxOnly,
            HighlightMatchDirection.Both => EventMatchDirection.Both,
            _ => EventMatchDirection.RxOnly
        };
    }

    private static void NormalizeCommandSequences(IList<CommandSequence> sequences, ICollection<string> warnings)
    {
        for (var sequenceIndex = sequences.Count - 1; sequenceIndex >= 0; sequenceIndex--)
        {
            var sequence = sequences[sequenceIndex];
            if (sequence is null)
            {
                sequences.RemoveAt(sequenceIndex);
                warnings.Add("A command sequence was invalid.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(sequence.Name))
            {
                sequence.Name = $"Sequence {sequenceIndex + 1}";
                warnings.Add("A command sequence name was missing.");
            }
            else
            {
                sequence.Name = sequence.Name.Trim();
            }

            sequence.Steps ??= new ObservableCollection<CommandSequenceStep>();
            for (var stepIndex = sequence.Steps.Count - 1; stepIndex >= 0; stepIndex--)
            {
                var step = sequence.Steps[stepIndex];
                if (step is null || string.IsNullOrWhiteSpace(step.CommandText))
                {
                    sequence.Steps.RemoveAt(stepIndex);
                    warnings.Add("A command sequence step was missing a command.");
                    continue;
                }

                step.CommandText = step.CommandText.Trim();
                step.Name = string.IsNullOrWhiteSpace(step.Name) ? null : step.Name.Trim();
                step.Comment = string.IsNullOrWhiteSpace(step.Comment) ? null : step.Comment.Trim();
                if (step.DelayAfterMs is < 0 or > MaxSequenceDelayAfterMs)
                {
                    step.DelayAfterMs = Math.Clamp(step.DelayAfterMs, 0, MaxSequenceDelayAfterMs);
                    warnings.Add("A command sequence delay was outside the safe range.");
                }

                if (step.LineEndingMode.HasValue && !Enum.IsDefined(step.LineEndingMode.Value))
                {
                    step.LineEndingMode = null;
                    warnings.Add("A command sequence line ending was invalid.");
                }
            }
        }
    }

}
