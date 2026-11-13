using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class LogViewModel
{
    private LogViewMemoryPolicy _memoryPolicy;
    private long _retainedMemoryBytes;
    private bool _trimmingForMemory;
    private long _memoryTrimBytes, _memoryTrimCharacters;

    public long RetainedMemoryBytes => _retainedMemoryBytes;
    public long RetainedMemoryByteLimit => _memoryPolicy.RetainedBytes;

    public void SetMemoryBudgetMiB(int budgetMiB)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetMiB, UiSettings.MinLogMemoryBudgetMiB);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budgetMiB, UiSettings.MaxLogMemoryBudgetMiB);
        var bytes = budgetMiB * 1024L * 1024;
        if (_memoryPolicy.RetainedBytes == bytes) return;
        _memoryPolicy = _memoryPolicy with { RetainedBytes = bytes };
        var version = ++_visibleRebuildVersion;
        var trimCharacters = 0;
        var trimmed = _retainedMemoryBytes > bytes || VisibleCharacterCount > _memoryPolicy.VisibleCharacters
            ? TrimScreenMemory(bytes * 99 / 100, _memoryPolicy.VisibleCharacters * 99 / 100,
                out trimCharacters, completePartial: false) : 0;
        DroppedVisibleLineCount += trimmed;
        OnPropertyChanged(nameof(RetainedMemoryByteLimit));
        RaiseVisibleCountProperties();
        NotifyPrefixTrim(trimmed, version, trimCharacters);
    }

    internal LogViewModel(int capacity, LogViewMemoryPolicy memoryPolicy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _memoryPolicy = memoryPolicy;
    }

    private bool NeedsMemoryTrim(long visibleCharacters) => _trimmingForMemory &&
        (_retainedMemoryBytes > _memoryTrimBytes || visibleCharacters > _memoryTrimCharacters);

    private int ReserveScreenMemory(RetainedLogLine retained, out int trimCharacters, StringBuilder? batch = null)
    {
        trimCharacters = 0;
        var line = retained.Line;
        var bytes = retained.EstimatedBytes;
        // Reserve room for control escaping and ANSI bracket colors before
        // rendering. Normal native records remain complete; oversized records
        // use the projection's defensive preview limit.
        var prefixCharacters = _viewFilters.Length == 0 ? 0 : retained.FilterPrefix.Length;
        var characters = 128L + Math.Min((long)line.Text.Length + prefixCharacters,
            _memoryPolicy.PreviewCharacters) * 24L;
        if (_rxDisplayMode == RxDisplayMode.Hex && line.Direction == LogDirection.Rx)
            characters = 128L + Math.Min(line.RawBytes?.LongLength ?? 0, _memoryPolicy.HexPreviewBytes) * 3L;
        if (_retainedMemoryBytes + bytes <= _memoryPolicy.RetainedBytes &&
            VisibleCharacterCount + characters <= _memoryPolicy.VisibleCharacters) return 0;
        // Leave 1% headroom to amortize cleanup without discarding a quarter of
        // the user's history. A large incoming record can require more room.
        return TrimScreenMemory(Math.Max(0, _memoryPolicy.RetainedBytes * 99 / 100 - bytes),
            Math.Max(0, _memoryPolicy.VisibleCharacters * 99 / 100 - characters), out trimCharacters, batch);
    }

    private int TrimScreenMemory(long bytes, long characters, out int trimCharacters, StringBuilder? batch = null,
        bool completePartial = true)
    {
        // This is a display boundary, not an RX/rule boundary. Preserve the live
        // streaming matcher suffix even when old screen history is evicted.
        if (completePartial) CompleteActivePartialRxVisualLine(batch);
        _trimmingForMemory = true;
        _memoryTrimBytes = bytes;
        _memoryTrimCharacters = characters;
        try { return TrimRetainedLinesToCapacity(out trimCharacters, preserveRuleState: true); }
        finally { _trimmingForMemory = false; }
    }

    private int TrimScreenMemoryAfterRebuild()
    {
        if (_retainedMemoryBytes <= _memoryPolicy.RetainedBytes &&
            VisibleCharacterCount <= _memoryPolicy.VisibleCharacters) return 0;
        return TrimScreenMemory(_memoryPolicy.RetainedBytes * 99 / 100,
            _memoryPolicy.VisibleCharacters * 99 / 100, out _);
    }

    private bool BreakLongScreenLine(int displayLength, int searchableLength, StringBuilder? batch = null)
    {
        // Wrap at original record boundaries. A single native completion can
        // exceed this continuation threshold without truncating its content.
        if (!_partialRxVisualLineActive) return false;
        var currentDisplay = _partialRxDisplayBuilder?.Length ?? _lastVisibleLine?.DisplayText.Length ?? 0;
        var currentSearch = _partialRxSearchableBuilder?.Length ?? _lastVisibleLine?.SearchText.Length ?? 0;
        return ((long)currentDisplay + displayLength > _memoryPolicy.ContinuousLineCharacters ||
                (long)currentSearch + searchableLength > _memoryPolicy.ContinuousLineCharacters) &&
            CompleteActivePartialRxVisualLine(batch);
    }
}
