using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ProtocolViewModel
{
    private ProtocolFrameFilter _filter;
    private string _filterUnitText = string.Empty;
    private string _filterFunctionText = string.Empty;
    private int _filterDirectionIndex, _filterStatusIndex;
    private string _filterValidationError = string.Empty;
    public bool HasActiveFilters => _filter.ActiveCount > 0;
    public string FilterLabel => HasActiveFilters
        ? UiText.Format("ProtocolFilterCountFormat", "Filter ({0})", _filter.ActiveCount)
        : UiText.Get("ProtocolFilter", "Filter");
    public string FilterTitle => UiText.Get("ProtocolFilterTitle", "Filter recent packets");
    public string FilterUnitLabel => UiText.Get("ProtocolFilterUnit", "Unit ID");
    public string FilterUnitPlaceholder => UiText.Get("ProtocolFilterUnitHint", "All, or 0–255 (decimal)");
    public string FilterFunctionLabel => "FC (HEX)";
    public string FilterFunctionPlaceholder => UiText.Get("ProtocolFilterFunctionHint", "All, or 03 / 0x03");
    public string FilterFunctionHelp => UiText.Get("ProtocolFilterFunctionHelp", "03 also includes exception 83. Enter 83 to show only that exception function.");
    public string FilterDirectionLabel => UiText.Get("ProtocolFilterDirection", "Direction");
    public string FilterStateLabel => UiText.Get("ProtocolState", "State");
    public string ApplyFiltersLabel => UiText.Get("ProtocolFilterApply", "Apply");
    public string ResetFiltersLabel => UiText.Get("ProtocolFilterReset", "Reset filters");
    public string FilterScopeHelp => IsDisplayPaused
        ? UiText.Get("ProtocolFilterPausedHelp", "These filters apply to the frozen list. Resume to include newly retained packets.")
        : UiText.Get("ProtocolFilterScopeHelp", "Filters affect this list only. Cumulative statistics include all analysis results.");
    public IReadOnlyList<string> FilterDirectionOptions { get; } = [UiText.Get("ProtocolFilterAllDirections", "All directions"), "RX", "TX"];
    public IReadOnlyList<string> FilterStatusOptions { get; } =
    [
        UiText.Get("ProtocolFilterAllStates", "All states"),
        UiText.Get("ProtocolFilterProblems", "CRC errors / exceptions"),
        UiText.Get("ProtocolStateCrcError", "CRC error"),
        UiText.Get("ProtocolStateException", "Exception"),
        UiText.Get("ProtocolStateIncomplete", "Incomplete"),
        UiText.Get("ProtocolStateUndecoded", "Undecoded"),
        UiText.Get("ProtocolFilterCrcValid", "CRC valid (incl. exceptions)")
    ];
    public string FilterUnitText { get => _filterUnitText; set => SetProperty(ref _filterUnitText, value ?? string.Empty); }
    public string FilterFunctionText { get => _filterFunctionText; set => SetProperty(ref _filterFunctionText, value ?? string.Empty); }
    public int FilterDirectionIndex { get => _filterDirectionIndex; set => SetProperty(ref _filterDirectionIndex, Math.Clamp(value, 0, 2)); }
    public int FilterStatusIndex { get => _filterStatusIndex; set => SetProperty(ref _filterStatusIndex, Math.Clamp(value, 0, FilterStatusOptions.Count - 1)); }
    public string FilterValidationError
    {
        get => _filterValidationError;
        private set { if (SetProperty(ref _filterValidationError, value)) OnPropertyChanged(nameof(FilterValidationVisibility)); }
    }
    public Visibility FilterValidationVisibility => string.IsNullOrEmpty(FilterValidationError) ? Visibility.Collapsed : Visibility.Visible;
    public string FilterSummary
    {
        get
        {
            if (!HasActiveFilters) return UiText.Get("ProtocolFilterOpenHelp", "Filter by unit ID, function, direction or analysis state.");
            var parts = new List<string>();
            if (_filter.UnitId is { } id) parts.Add($"ID {id}");
            if (_filter.FunctionCode is { } fc) parts.Add(fc < 0x80 ? $"FC {fc:X2} (+{fc | 0x80:X2})" : $"FC {fc:X2}");
            if (_filter.Direction is { } direction) parts.Add(direction == ProtocolDirection.Rx ? "RX" : "TX");
            if (_filter.Status != ProtocolStatusFilter.All) parts.Add(FilterStatusOptions[(int)_filter.Status]);
            return string.Join(" · ", parts);
        }
    }

    // Opening the editor restores applied conditions. Closing without Apply
    // cancels draft changes and never leaves an invisible partial filter active.
    public void BeginFilterEdit()
    {
        FilterUnitText = _filter.UnitId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        FilterFunctionText = _filter.FunctionCode?.ToString("X2", CultureInfo.InvariantCulture) ?? string.Empty;
        FilterDirectionIndex = _filter.Direction switch { ProtocolDirection.Rx => 1, ProtocolDirection.Tx => 2, _ => 0 };
        FilterStatusIndex = (int)_filter.Status;
        FilterValidationError = string.Empty;
        OnPropertyChanged(nameof(FilterScopeHelp));
    }

    public bool TryApplyFilters()
    {
        byte? unit = null, function = null;
        var unitText = FilterUnitText.Trim();
        if (unitText.Length > 0)
        {
            if (!byte.TryParse(unitText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                FilterValidationError = UiText.Get("ProtocolFilterUnitError", "Enter a decimal unit ID from 0 to 255, or leave it empty.");
                return false;
            }
            unit = parsed;
        }
        var functionText = FilterFunctionText.Trim();
        if (functionText.Length > 0)
        {
            if (functionText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) functionText = functionText[2..];
            if (functionText.Length is < 1 or > 2 || !byte.TryParse(functionText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
            {
                FilterValidationError = UiText.Get("ProtocolFilterFunctionError", "Enter a function code in HEX from 00 to FF, or leave it empty.");
                return false;
            }
            function = parsed;
        }
        var direction = FilterDirectionIndex switch { 1 => (ProtocolDirection?)ProtocolDirection.Rx, 2 => ProtocolDirection.Tx, _ => null };
        _filter = new ProtocolFrameFilter(unit, function, direction, (ProtocolStatusFilter)FilterStatusIndex);
        FilterValidationError = string.Empty;
        FiltersChanged();
        return true;
    }

    public void ResetFilters()
    {
        _filter = default;
        BeginFilterEdit();
        FiltersChanged();
    }

    private void FiltersChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(FilterLabel));
        OnPropertyChanged(nameof(FilterSummary));
        Refresh(force: true);
    }
}
