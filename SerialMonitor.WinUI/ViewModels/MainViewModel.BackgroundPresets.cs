using System.Globalization;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IBackgroundImageStore _backgroundImages = new BackgroundImageStore();
    private Task? _backgroundImageImportTask;
    private long _cuteBackgroundImageRevision;

    public AsyncRelayCommand SelectBackgroundPresetCommand { get; private set; } = null!;

    public int SelectedBackgroundPreset => _currentUiSettings.SelectedBackgroundPreset;

    public bool IsBackgroundPreset1Selected => SelectedBackgroundPreset == 1;
    public bool IsBackgroundPreset2Selected => SelectedBackgroundPreset == 2;
    public bool IsBackgroundPreset3Selected => SelectedBackgroundPreset == 3;
    public bool IsBackgroundPreset4Selected => SelectedBackgroundPreset == 4;
    public bool IsBackgroundPreset5Selected => SelectedBackgroundPreset == 5;

    public bool CanEditBackgroundPresets => !IsBusy && !_isProfileSaveActive && !IsAutomationBusy && !IsShuttingDown;

    public long CuteBackgroundImageRevision => _cuteBackgroundImageRevision;

    private async Task SelectBackgroundPresetAsync(object? parameter)
    {
        if (!CanEditBackgroundPresets || !int.TryParse(parameter?.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) || number is < 1 or > UiSettings.BackgroundPresetCount) return;

        _currentUiSettings.EnsureBackgroundPresets();
        _currentUiSettings.SelectedBackgroundPreset = number;
        _currentUiSettings.ApplySelectedBackgroundPreset();
        NotifyBackgroundPresetPropertiesChanged();
        RecordSettingsChange("Background preset", SettingsApplyBehavior.Immediate, number.ToString(CultureInfo.InvariantCulture));
        await SaveProfileAsync();
    }

    public Task SetCuteBackgroundImagePathFromPickerAsync(string? path, int presetNumber)
    {
        if (string.IsNullOrWhiteSpace(path) || !CanEditBackgroundPresets) return Task.CompletedTask;
        return _backgroundImageImportTask = ImportBackgroundImageAsync(path, presetNumber);
    }

    private async Task ImportBackgroundImageAsync(string path, int presetNumber)
    {
        _isProfileSaveActive = true;
        NotifyCommandStates();
        try
        {
            var token = _settingsValidationCancellation.Token;
            var storedPath = await _backgroundImages.ImportAsync(CurrentProfilePath, presetNumber, path, token);
            token.ThrowIfCancellationRequested();
            _currentUiSettings.EnsureBackgroundPresets();
            var previousPath = CuteBackgroundImagePath;
            _currentUiSettings.BackgroundPresets![presetNumber - 1].ImagePath = storedPath;
            _currentUiSettings.ApplySelectedBackgroundPreset();
            NotifyBackgroundPresetPropertiesChanged();
            // Replacing a slot can keep the same managed filename.
            if (string.Equals(previousPath, CuteBackgroundImagePath, StringComparison.Ordinal)) InvalidateBackgroundImage();
            RecordSettingsChange("Background preset image", SettingsApplyBehavior.Immediate, $"preset {presetNumber}");
            await _profileService.SaveAsync(CurrentProfilePath, CreateProfileFromCurrentState(), token);
            SetStatus(UiText.Format("BackgroundPresetSaved", "Background preset {0} saved. The original image can be deleted.", presetNumber));
        }
        catch (OperationCanceledException) when (_settingsValidationCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _lastBackgroundError = $"Background preset {presetNumber} could not be saved: {ex.Message}";
            SetStatus(_lastBackgroundError);
            RuntimeDiagnostics.RecordError("BackgroundPreset.Import", ex);
        }
        finally
        {
            _isProfileSaveActive = false;
            RefreshProfileProperties();
            NotifyCommandStates();
            RefreshStatusBar();
        }
    }

    private void InvalidateBackgroundImage()
    {
        _cuteBackgroundImageRevision++;
        OnPropertyChanged(nameof(CuteBackgroundImageRevision));
    }

    private void NotifyBackgroundPresetPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedBackgroundPreset));
        OnPropertyChanged(nameof(IsBackgroundPreset1Selected));
        OnPropertyChanged(nameof(IsBackgroundPreset2Selected));
        OnPropertyChanged(nameof(IsBackgroundPreset3Selected));
        OnPropertyChanged(nameof(IsBackgroundPreset4Selected));
        OnPropertyChanged(nameof(IsBackgroundPreset5Selected));
        OnPropertyChanged(nameof(CuteBackgroundImagePath));
        OnPropertyChanged(nameof(CuteBackgroundOpacity));
        OnPropertyChanged(nameof(CuteBackgroundOpacityPercent));
    }
}
