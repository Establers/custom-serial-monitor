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

    public AsyncRelayCommand SelectBackgroundAnchorCommand { get; private set; } = null!;

    public int SelectedBackgroundPreset => _currentUiSettings.SelectedBackgroundPreset;

    public bool IsBackgroundPreset1Selected => SelectedBackgroundPreset == 1;
    public bool IsBackgroundPreset2Selected => SelectedBackgroundPreset == 2;
    public bool IsBackgroundPreset3Selected => SelectedBackgroundPreset == 3;
    public bool IsBackgroundPreset4Selected => SelectedBackgroundPreset == 4;
    public bool IsBackgroundPreset5Selected => SelectedBackgroundPreset == 5;

    public bool CanEditBackgroundPresets => !IsBusy && !_isProfileSaveActive && !IsAutomationBusy && !IsShuttingDown;

    public long CuteBackgroundImageRevision => _cuteBackgroundImageRevision;

    private BackgroundPreset? CurrentBackgroundPreset =>
        _currentUiSettings.BackgroundPresets?.ElementAtOrDefault(SelectedBackgroundPreset - 1);

    public BackgroundFitMode BackgroundFit => CurrentBackgroundPreset?.FitMode ?? BackgroundFitMode.Cover;

    public BackgroundAnchor BackgroundPosition => CurrentBackgroundPreset?.Anchor ?? BackgroundAnchor.Center;

    public int SelectedBackgroundFitIndex
    {
        get => (int)BackgroundFit;
        set
        {
            if (!CanEditBackgroundPresets || !Enum.IsDefined((BackgroundFitMode)value) || value == (int)BackgroundFit) return;
            _currentUiSettings.EnsureBackgroundPresets();
            CurrentBackgroundPreset!.FitMode = (BackgroundFitMode)value;
            NotifyBackgroundPlacementPropertiesChanged();
            RecordSettingsChange("Background fit", SettingsApplyBehavior.Immediate, BackgroundFit.ToString());
        }
    }

    public bool CanEditBackgroundPosition => CanEditBackgroundPresets && BackgroundFit == BackgroundFitMode.Cover;

    public bool IsBackgroundTopLeftSelected { get => BackgroundPosition == BackgroundAnchor.TopLeft; set => SetBackgroundPosition(value, BackgroundAnchor.TopLeft); }
    public bool IsBackgroundTopSelected { get => BackgroundPosition == BackgroundAnchor.Top; set => SetBackgroundPosition(value, BackgroundAnchor.Top); }
    public bool IsBackgroundTopRightSelected { get => BackgroundPosition == BackgroundAnchor.TopRight; set => SetBackgroundPosition(value, BackgroundAnchor.TopRight); }
    public bool IsBackgroundLeftSelected { get => BackgroundPosition == BackgroundAnchor.Left; set => SetBackgroundPosition(value, BackgroundAnchor.Left); }
    public bool IsBackgroundCenterSelected { get => BackgroundPosition == BackgroundAnchor.Center; set => SetBackgroundPosition(value, BackgroundAnchor.Center); }
    public bool IsBackgroundRightSelected { get => BackgroundPosition == BackgroundAnchor.Right; set => SetBackgroundPosition(value, BackgroundAnchor.Right); }
    public bool IsBackgroundBottomLeftSelected { get => BackgroundPosition == BackgroundAnchor.BottomLeft; set => SetBackgroundPosition(value, BackgroundAnchor.BottomLeft); }
    public bool IsBackgroundBottomSelected { get => BackgroundPosition == BackgroundAnchor.Bottom; set => SetBackgroundPosition(value, BackgroundAnchor.Bottom); }
    public bool IsBackgroundBottomRightSelected { get => BackgroundPosition == BackgroundAnchor.BottomRight; set => SetBackgroundPosition(value, BackgroundAnchor.BottomRight); }

    private Task SelectBackgroundAnchorAsync(object? parameter)
    {
        if (int.TryParse(parameter?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            SetBackgroundPosition(true, (BackgroundAnchor)index);
        return Task.CompletedTask;
    }

    private void SetBackgroundPosition(bool selected, BackgroundAnchor position)
    {
        if (!selected || !CanEditBackgroundPosition || !Enum.IsDefined(position) || position == BackgroundPosition) return;
        _currentUiSettings.EnsureBackgroundPresets();
        CurrentBackgroundPreset!.Anchor = position;
        NotifyBackgroundPlacementPropertiesChanged();
        RecordSettingsChange("Background position", SettingsApplyBehavior.Immediate, BackgroundPosition.ToString());
    }

    private void NotifyBackgroundPlacementPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedBackgroundFitIndex));
        OnPropertyChanged(nameof(BackgroundFit));
        OnPropertyChanged(nameof(BackgroundPosition));
        OnPropertyChanged(nameof(CanEditBackgroundPosition));
        OnPropertyChanged(nameof(IsBackgroundTopLeftSelected));
        OnPropertyChanged(nameof(IsBackgroundTopSelected));
        OnPropertyChanged(nameof(IsBackgroundTopRightSelected));
        OnPropertyChanged(nameof(IsBackgroundLeftSelected));
        OnPropertyChanged(nameof(IsBackgroundCenterSelected));
        OnPropertyChanged(nameof(IsBackgroundRightSelected));
        OnPropertyChanged(nameof(IsBackgroundBottomLeftSelected));
        OnPropertyChanged(nameof(IsBackgroundBottomSelected));
        OnPropertyChanged(nameof(IsBackgroundBottomRightSelected));
        SelectBackgroundAnchorCommand.NotifyCanExecuteChanged();
    }

    public double XtermBackgroundOpacity
    {
        get => _currentUiSettings.XtermBackgroundOpacity;
        set
        {
            if (!double.IsFinite(value)) return;
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_currentUiSettings.XtermBackgroundOpacity - clamped) < 0.0001) return;

            _currentUiSettings.EnsureBackgroundPresets();
            _currentUiSettings.XtermBackgroundOpacity = clamped;
            _currentUiSettings.BackgroundPresets![SelectedBackgroundPreset - 1].XtermOpacity = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(XtermBackgroundOpacityPercent));
            OnPropertyChanged(nameof(XtermBackgroundTransparencyPercent));
            RecordSettingsChange("xterm background opacity", SettingsApplyBehavior.Immediate,
                $"{XtermBackgroundOpacityPercent}%");
            RefreshStatusBar();
        }
    }

    public int XtermBackgroundOpacityPercent
    {
        get => (int)Math.Round(XtermBackgroundOpacity * 100, MidpointRounding.AwayFromZero);
        set => XtermBackgroundOpacity = Math.Clamp(value, 0, 100) / 100.0;
    }

    public int XtermBackgroundTransparencyPercent
    {
        get => 100 - XtermBackgroundOpacityPercent;
        set => XtermBackgroundOpacityPercent = 100 - Math.Clamp(value, 0, 100);
    }

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
        NotifyBackgroundPlacementPropertiesChanged();
        OnPropertyChanged(nameof(SelectedBackgroundPreset));
        OnPropertyChanged(nameof(IsBackgroundPreset1Selected));
        OnPropertyChanged(nameof(IsBackgroundPreset2Selected));
        OnPropertyChanged(nameof(IsBackgroundPreset3Selected));
        OnPropertyChanged(nameof(IsBackgroundPreset4Selected));
        OnPropertyChanged(nameof(IsBackgroundPreset5Selected));
        OnPropertyChanged(nameof(CuteBackgroundImagePath));
        OnPropertyChanged(nameof(CuteBackgroundOpacity));
        OnPropertyChanged(nameof(CuteBackgroundOpacityPercent));
        OnPropertyChanged(nameof(CuteBackgroundTransparencyPercent));
        OnPropertyChanged(nameof(XtermBackgroundOpacity));
        OnPropertyChanged(nameof(XtermBackgroundOpacityPercent));
        OnPropertyChanged(nameof(XtermBackgroundTransparencyPercent));
    }
}
