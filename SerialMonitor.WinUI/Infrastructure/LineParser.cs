using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class RawLogLine
{
    public RawLogLine(byte[] bytes, bool isPartial = false, bool isPartialTerminator = false)
    {
        Bytes = bytes;
        IsPartial = isPartial;
        IsPartialTerminator = isPartialTerminator;
    }

    public byte[] Bytes { get; }

    public bool IsPartial { get; }

    public bool IsPartialTerminator { get; }

    public static RawLogLine PartialTerminator { get; } =
        new(Array.Empty<byte>(), isPartialTerminator: true);
}

public sealed class LineParser
{
    private readonly List<byte> _pending = new();
    private bool _autoPreviousWasCr;
    private bool _suppressNextEmptyTerminatorLine;

    public int PartialBufferLength => _pending.Count;

    public IReadOnlyList<RawLogLine> Append(ReadOnlyMemory<byte> bytes, RxLineEndingMode mode)
    {
        if (bytes.IsEmpty)
        {
            return Array.Empty<RawLogLine>();
        }

        return mode switch
        {
            RxLineEndingMode.Cr => AppendSingleTerminator(bytes.Span, (byte)'\r'),
            RxLineEndingMode.Lf => AppendSingleTerminator(bytes.Span, (byte)'\n'),
            RxLineEndingMode.Crlf => AppendCrlf(bytes.Span),
            _ => AppendAuto(bytes.Span)
        };
    }

    public void Clear()
    {
        _pending.Clear();
        _autoPreviousWasCr = false;
        _suppressNextEmptyTerminatorLine = false;
    }

    public RawLogLine? FlushPartial(RxLineEndingMode mode)
    {
        var flushLength = _pending.Count;
        if (flushLength == 0)
        {
            return null;
        }

        if (mode == RxLineEndingMode.Crlf && _pending[^1] == '\r')
        {
            flushLength--;
        }

        if (flushLength <= 0)
        {
            return null;
        }

        var line = _pending.GetRange(0, flushLength).ToArray();
        _pending.RemoveRange(0, flushLength);
        _suppressNextEmptyTerminatorLine = true;
        return new RawLogLine(line, isPartial: true);
    }

    private IReadOnlyList<RawLogLine> AppendAuto(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<RawLogLine>();

        foreach (var value in bytes)
        {
            if (value == '\r')
            {
                AddTerminatedLine(lines);
                _autoPreviousWasCr = true;
                continue;
            }

            if (value == '\n')
            {
                if (_autoPreviousWasCr)
                {
                    _autoPreviousWasCr = false;
                    continue;
                }

                AddTerminatedLine(lines);
                continue;
            }

            _autoPreviousWasCr = false;
            _pending.Add(value);
        }

        return lines;
    }

    private IReadOnlyList<RawLogLine> AppendSingleTerminator(ReadOnlySpan<byte> bytes, byte terminator)
    {
        var lines = new List<RawLogLine>();
        _autoPreviousWasCr = false;

        foreach (var value in bytes)
        {
            if (value == terminator)
            {
                AddTerminatedLine(lines);
                continue;
            }

            _pending.Add(value);
        }

        return lines;
    }

    private IReadOnlyList<RawLogLine> AppendCrlf(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<RawLogLine>();
        _autoPreviousWasCr = false;

        foreach (var value in bytes)
        {
            _pending.Add(value);
            if (value == '\n' && _pending.Count >= 2 && _pending[^2] == '\r')
            {
                var lineLength = _pending.Count - 2;
                var line = _pending.GetRange(0, lineLength).ToArray();
                _pending.Clear();
                if (line.Length == 0 && _suppressNextEmptyTerminatorLine)
                {
                    _suppressNextEmptyTerminatorLine = false;
                    lines.Add(RawLogLine.PartialTerminator);
                    continue;
                }

                _suppressNextEmptyTerminatorLine = false;
                lines.Add(new RawLogLine(line));
            }
        }

        return lines;
    }

    private void AddTerminatedLine(List<RawLogLine> lines)
    {
        if (_pending.Count == 0 && _suppressNextEmptyTerminatorLine)
        {
            _suppressNextEmptyTerminatorLine = false;
            lines.Add(RawLogLine.PartialTerminator);
            return;
        }

        _suppressNextEmptyTerminatorLine = false;
        lines.Add(FlushPending());
    }

    private RawLogLine FlushPending()
    {
        var line = _pending.ToArray();
        _pending.Clear();
        return new RawLogLine(line);
    }
}
