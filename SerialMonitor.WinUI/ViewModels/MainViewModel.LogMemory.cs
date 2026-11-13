using System.Globalization;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public int LogMemoryBudgetMiB
    {
        get => _currentUiSettings.LogMemoryBudgetMiB;
        set
        {
            if (!ValidateIntRange("Log memory budget (MiB)", value,
                    UiSettings.MinLogMemoryBudgetMiB, UiSettings.MaxLogMemoryBudgetMiB))
            {
                OnPropertyChanged(nameof(LogMemoryBudgetMiBText));
                return;
            }
            if (_currentUiSettings.LogMemoryBudgetMiB == value) return;
            _currentUiSettings.LogMemoryBudgetMiB = value;
            SetVisibleLogRebuildReason("visible log memory budget change");
            Log.SetMemoryBudgetMiB(value);
            MarkSearchResultsStale();
            RecordSettingsChange("Log memory budget (MiB)", SettingsApplyBehavior.Immediate,
                value.ToString(CultureInfo.InvariantCulture));
            OnPropertyChanged();
            OnPropertyChanged(nameof(LogMemoryBudgetMiBText));
            RefreshStatusBar();
        }
    }

    public string LogMemoryBudgetMiBText
    {
        get => LogMemoryBudgetMiB.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseIntSetting("Log memory budget (MiB)", value,
                    UiSettings.MinLogMemoryBudgetMiB, UiSettings.MaxLogMemoryBudgetMiB, out var parsed))
                LogMemoryBudgetMiB = parsed;
            else OnPropertyChanged();
        }
    }
}
