using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class LogViewModel
{
    private readonly LogViewMemoryPolicy _memoryPolicy;
    private long _retainedMemoryBytes;
    private bool _trimmingForMemory;
    private long _memoryTrimBytes, _memoryTrimCharacters;

    public long RetainedMemoryBytes => _retainedMemoryBytes;
    public long RetainedMemoryByteLimit => _memoryPolicy.RetainedBytes;

    internal LogViewModel(int capacity, LogViewMemoryPolicy memoryPolicy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _memoryPolicy = memoryPolicy;
    }

    private bool NeedsMemoryTrim(long visibleCharacters) => _trimmingForMemory &&
        (_retainedMemoryBytes > _memoryTrimBytes || visibleCharacters > _memoryTrimCharacters);

    private int ReserveScreenMemory(RetainedLogLine retained)
    {
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
        return TrimScreenMemory(Math.Max(0, _memoryPolicy.RetainedBytes * 3 / 4 - bytes),
            Math.Max(0, _memoryPolicy.VisibleCharacters * 3 / 4 - characters));
    }

    private int TrimScreenMemory(long bytes, long characters)
    {
        // This is a display boundary, not an RX/rule boundary. Preserve the live
        // streaming matcher suffix even when old screen history is evicted.
        CompleteActivePartialRxVisualLine();
        _trimmingForMemory = true;
        _memoryTrimBytes = bytes;
        _memoryTrimCharacters = characters;
        try { return TrimRetainedLinesToCapacity(out _, preserveRuleState: true); }
        finally { _trimmingForMemory = false; }
    }

    private int TrimScreenMemoryAfterRebuild()
    {
        if (_retainedMemoryBytes <= _memoryPolicy.RetainedBytes &&
            VisibleCharacterCount <= _memoryPolicy.VisibleCharacters) return 0;
        return TrimScreenMemory(_memoryPolicy.RetainedBytes * 3 / 4,
            _memoryPolicy.VisibleCharacters * 3 / 4);
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
