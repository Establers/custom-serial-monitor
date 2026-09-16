namespace SerialMonitor.WinUI.Models;

// Fixed-size run telemetry. Mutated by the sequence callbacks on the UI context;
// displayed by the existing status timer, independently of the selected sequence.
internal sealed class SequenceTxActivity(TimeProvider? clock = null)
{
    internal const int MaxNamePreviewLength = 80;
    internal const int MaxCommandPreviewLength = 160;
    internal const int MaxPhasePreviewLength = 160;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string _name = string.Empty;
    private string _position = string.Empty;
    private string _phase = "No run yet";
    private string _lastCommand = string.Empty;
    private int _total;
    private int _sent;
    private long _failedAttempts;
    private DateTimeOffset? _lastSent;
    private long _lastSentTimestamp;
    private int _delayMs;

    public void Begin(string name, int total)
    {
        _name = Preview(name, MaxNamePreviewLength);
        _total = total;
        _sent = 0;
        _failedAttempts = 0;
        _lastSent = null;
        _lastCommand = string.Empty;
        _position = string.Empty;
        _phase = "Starting";
        _delayMs = 0;
    }

    public void Sending(int position, int stepsPerRepeat)
    {
        _position = $"Repeat {position / stepsPerRepeat + 1:N0} · Step {position % stepsPerRepeat + 1:N0}/{stepsPerRepeat:N0}";
        _phase = "Sending";
    }

    public void Sent(string command, int delayMs)
    {
        _sent++;
        _lastCommand = Preview(command, MaxCommandPreviewLength);
        _lastSent = _clock.GetLocalNow();
        _lastSentTimestamp = _clock.GetTimestamp();
        _delayMs = delayMs;
        _phase = "Delay";
    }

    public void FailedAttempt()
    {
        _failedAttempts++;
        _phase = "TX failed";
    }

    public void SetPhase(string phase) => _phase = Preview(phase, MaxPhasePreviewLength);

    // Slice before inspecting characters: work and retained memory are bounded even
    // when the original command is megabytes long. Never alter the actual TX payload.
    private static string Preview(string value, int limit)
    {
        var length = Math.Min(value.Length, limit);
        if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        var preview = value[..length];
        preview = string.Create(preview.Length, preview, (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = char.IsControl(source[i]) || source[i] is '\u2028' or '\u2029' ? ' ' : source[i];
        });
        return length < value.Length ? preview + "…" : preview;
    }

    public string Summary
    {
        get
        {
            if (_name.Length == 0) return "TX: no sequence run yet";
            var age = _lastSent.HasValue ? _clock.GetElapsedTime(_lastSentTimestamp).TotalSeconds : 0;
            var last = _lastSent.HasValue ? $"{_lastSent:MM-dd HH:mm:ss} ({Math.Floor(age):N0}s ago)" : "none";
            var phase = _phase == "Delay"
                ? $"Delay {Math.Max(0, Math.Ceiling(_delayMs / 1000d - age)):N0}s remaining"
                : _phase;
            return $"{_name} | TX OK {_sent:N0}/{_total:N0} · Failed {_failedAttempts:N0} | Last TX {last} | {_position} · {phase}";
        }
    }

    public string Details => $"{Summary}\nLast successful command: {(_lastSent.HasValue ? _lastCommand : "(none)")}\nLong names, commands and errors are shown as short previews (…). TX OK means the app's serial send completed. Device receipt/execution is not verified (no ACK check). Failed counts failed send attempts, including retries. Counters reset on each new run; the last result remains after stopping.";
}
