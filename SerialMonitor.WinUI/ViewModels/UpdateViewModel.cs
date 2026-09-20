using System.Reflection;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public enum UpdateCheckState { Idle, Checking, Current, Available, Failed }

// UI state only. Network access and preference storage belong to IUpdateService.
public sealed class UpdateViewModel : ViewModelBase, IDisposable
{
    private readonly IUpdateService _service;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Version _current;
    private UpdatePreferences _preferences = new();
    private AppRelease? _release;
    private bool _initialized;
    private bool _manualReveal;
    private bool _disposed;
    private bool _checkInProgress;
    private long _preferenceRevision;
    private long _automaticRevision;
    private long _skipRevision;
    private bool _savingAutomatic;
    private bool _automaticSavePending;
    private bool _automaticDirty;
    private bool _skipDirty;
    private string _preferenceError = string.Empty;

    public UpdateViewModel(IUpdateService? service = null, Version? current = null)
    {
        _service = service ?? new UpdateService();
        var version = current ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        _current = new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(true), () => IsReady && !IsChecking && !_disposed);
        SkipCommand = new AsyncRelayCommand(SkipAsync, () => _release is not null && !IsChecking && !_disposed);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => _release is not null && !_disposed);
    }

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand SkipCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public bool IsReady { get; private set; }
    public UpdateCheckState State { get; private set; }
    public bool IsChecking => _checkInProgress;
    public string InstalledVersion => $"v{_current.ToString(3)}";
    public bool IsSkipped => _release is not null && _preferences.SkippedVersion == _release.Version.ToString();
    public bool ShowNotification => _release is not null && (!IsSkipped || _manualReveal);
    public string NotificationVisibility => ShowNotification ? "Visible" : "Collapsed";
    public string ReleaseVisibility => _release is not null ? "Visible" : "Collapsed";
    public string ProgressVisibility => IsChecking ? "Visible" : "Collapsed";
    public string StatusGlyphVisibility => IsChecking ? "Collapsed" : "Visible";
    public string PreferenceErrorVisibility => string.IsNullOrEmpty(_preferenceError) ? "Collapsed" : "Visible";
    public string PreferenceError => _preferenceError;
    public string NotificationText => UiText.Format("UpdateBadge", "Update {0}", _release?.Tag ?? "");
    public string CheckButtonText => IsChecking ? T("UpdateCheckingButton", "Checking…") : T("UpdateCheckButton", "Check for updates");
    public string Status => State switch
    {
        UpdateCheckState.Checking => T("UpdateChecking", "Checking for updates…"),
        UpdateCheckState.Failed => T("UpdateFailed", "Could not check for updates. Try again when you’re online."),
        UpdateCheckState.Available when IsSkipped => UiText.Format("UpdateSkipped", "{0} is available · automatic notification skipped", _release?.Tag),
        UpdateCheckState.Available => UiText.Format("UpdateAvailable", "{0} is available", _release?.Tag),
        UpdateCheckState.Current => T("UpdateCurrent", "You’re up to date"),
        _ => T("UpdateIdle", "Check for a newer version")
    };
    public string StatusGlyph => State switch
    {
        UpdateCheckState.Current => "\uE73E",
        UpdateCheckState.Available => "\uE898",
        UpdateCheckState.Failed => "\uE783",
        _ => "\uE946"
    };
    public string LastCheckedText => _preferences.LastSuccessUtc is { } last
        ? UiText.Format("UpdateLastChecked", "Last checked {0}", last.LocalDateTime.ToString("g"))
        : T("UpdateNotChecked", "Not checked yet");
    public bool Automatic
    {
        get => _preferences.Automatic;
        set
        {
            if (value == _preferences.Automatic || !IsReady || _disposed) return;
            _preferences.Automatic = value;
            _preferenceRevision++;
            _automaticRevision++;
            _automaticDirty = true;
            OnPropertyChanged();
            _automaticSavePending = true;
            if (!_savingAutomatic) _ = SaveAutomaticAsync();
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        await LoadPreferencesAsync();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token);
            if (UpdateService.IsAutomaticCheckDue(_preferences, DateTimeOffset.UtcNow)) await CheckAsync(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    internal async Task LoadPreferencesAsync()
    {
        try
        {
            _preferences = await _service.LoadAsync(_shutdown.Token);
            if (_disposed) return;
            if (_preferences.LastKnownTag is { } tag && UpdateService.ParseVersion(tag) is { } version)
            {
                _release = version > _current ? UpdateService.CreateRelease(tag, version) : null;
                State = _release is null ? UpdateCheckState.Current : UpdateCheckState.Available;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _preferences.Automatic = false;
            _preferenceError = T("UpdateLoadFailed", "Could not load preferences. Automatic checks are off.");
            RuntimeDiagnostics.RecordError("Updates.Initialize", ex);
        }
        IsReady = true;
        Refresh();
    }

    internal async Task CheckAsync(bool manual)
    {
        if (IsChecking || _disposed) return;
        _checkInProgress = true;
        State = UpdateCheckState.Checking;
        Refresh();
        try
        {
            if (!manual)
            {
                var revision = _preferenceRevision;
                var latest = await _service.LoadAsync(_shutdown.Token);
                if (_disposed) return;
                if (revision == _preferenceRevision && !_savingAutomatic)
                {
                    if (!_automaticDirty) _preferences.Automatic = latest.Automatic;
                    if (!_skipDirty) _preferences.SkippedVersion = latest.SkippedVersion;
                    _preferences.LastAttemptUtc = latest.LastAttemptUtc;
                }
                MergeCachedRelease(latest);
                if (!UpdateService.IsAutomaticCheckDue(_preferences, DateTimeOffset.UtcNow))
                {
                    State = _release is not null ? UpdateCheckState.Available
                        : _preferences.LastSuccessUtc is not null ? UpdateCheckState.Current : UpdateCheckState.Idle;
                    return;
                }
            }
            var startedAt = DateTimeOffset.UtcNow;
            _preferences.LastAttemptUtc = startedAt;
            await SaveAsync(new(LastAttemptUtc: startedAt));
            var release = await _service.CheckAsync(_shutdown.Token);
            if (_disposed) return;
            _release = release.Version > _current ? release : null;
            _manualReveal = manual;
            _preferences.LastKnownTag = release.Tag;
            _preferences.LastSuccessUtc = startedAt;
            await SaveAsync(new(LastSuccessUtc: startedAt, LastKnownTag: release.Tag));
            State = _release is null ? UpdateCheckState.Current : UpdateCheckState.Available;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            State = UpdateCheckState.Failed;
            RuntimeDiagnostics.RecordError("Updates.Check", ex);
        }
        finally { _checkInProgress = false; if (!_disposed) Refresh(); }
    }

    private void MergeCachedRelease(UpdatePreferences latest)
    {
        // Another window may have completed a check during our startup delay.
        // Adopt its result before the daily limit suppresses our network request.
        if (latest.LastSuccessUtc is not { } success ||
            (_preferences.LastSuccessUtc is { } currentSuccess && success < currentSuccess) ||
            latest.LastKnownTag is not { } tag || UpdateService.ParseVersion(tag) is not { } version) return;

        _preferences.LastSuccessUtc = success;
        _preferences.LastKnownTag = tag;
        _release = version > _current ? UpdateService.CreateRelease(tag, version) : null;
    }

    private async Task SaveAutomaticAsync()
    {
        _savingAutomatic = true;
        try
        {
            // At most one active write and one latest pending value, even under rapid toggles.
            while (_automaticSavePending && !_disposed)
            {
                _automaticSavePending = false;
                await SaveAsync(new(Automatic: _preferences.Automatic));
            }
        }
        finally { _savingAutomatic = false; }
    }

    private async Task<bool> SaveAsync(UpdatePreferenceChange change)
    {
        try
        {
            var automaticRevision = _automaticRevision;
            var skipRevision = _skipRevision;
            var saved = await _service.ApplyAsync(change, _shutdown.Token);
            if (_disposed) return false;
            // Preserve local edits made while this write was in flight.
            if (automaticRevision == _automaticRevision)
            {
                if (change.Automatic is not null) _automaticDirty = false;
                if (!_automaticDirty && !_automaticSavePending) _preferences.Automatic = saved.Automatic;
                OnPropertyChanged(nameof(Automatic));
            }
            if (skipRevision == _skipRevision)
            {
                if (change.SkippedVersion is not null) _skipDirty = false;
                if (!_skipDirty && _preferences.SkippedVersion != saved.SkippedVersion)
                {
                    _preferences.SkippedVersion = saved.SkippedVersion;
                    // Automatic preference saves do not otherwise refresh the release UI.
                    foreach (var property in new[] { nameof(IsSkipped), nameof(ShowNotification),
                        nameof(NotificationVisibility), nameof(Status) }) OnPropertyChanged(property);
                }
            }
            if (!_automaticDirty && !_skipDirty) _preferenceError = string.Empty;
            return true;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            _preferenceError = T("UpdateSaveFailed", "Preferences could not be saved. Changes apply only to this session.");
            RuntimeDiagnostics.RecordError("Updates.Save", ex);
            return false;
        }
        finally
        {
            if (!_disposed)
            {
                OnPropertyChanged(nameof(PreferenceError));
                OnPropertyChanged(nameof(PreferenceErrorVisibility));
            }
        }
    }

    internal async Task SkipAsync()
    {
        if (_release is null || IsChecking || _disposed) return;
        _preferences.SkippedVersion = _release.Version.ToString();
        _manualReveal = false;
        _preferenceRevision++;
        _skipRevision++;
        _skipDirty = true;
        await SaveAsync(new(SkippedVersion: _preferences.SkippedVersion));
        if (!_disposed) Refresh();
    }

    private async Task OpenAsync()
    {
        if (_release is null || _disposed) return;
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(_release.Page))
                throw new InvalidOperationException("No browser accepted the release URI.");
        }
        catch (Exception ex)
        {
            _preferenceError = T("UpdateOpenFailed", "Could not open your browser. Visit the GitHub link below for releases.");
            RuntimeDiagnostics.RecordError("Updates.Open", ex);
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var property in new[] { nameof(State), nameof(IsReady), nameof(IsChecking), nameof(Automatic),
            nameof(Status), nameof(StatusGlyph), nameof(CheckButtonText), nameof(IsSkipped), nameof(ShowNotification),
            nameof(NotificationVisibility), nameof(NotificationText), nameof(ReleaseVisibility), nameof(ProgressVisibility), nameof(StatusGlyphVisibility),
            nameof(LastCheckedText), nameof(PreferenceError), nameof(PreferenceErrorVisibility) }) OnPropertyChanged(property);
        CheckCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
    }

    private static string T(string key, string fallback) => UiText.Get(key, fallback);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
    }
}
