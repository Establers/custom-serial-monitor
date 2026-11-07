using System.ComponentModel;
using System.Diagnostics;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

// One bounded background observer per connection. Neither polling nor driver
// writes run on the UI/RX threads. A single gate serializes all control IO.
internal sealed class SerialControlLineRuntime
{
    private readonly ISerialControlLinePort? _port;
    private readonly Action _changed;
    private readonly CancellationToken _lifetime;
    private readonly long _generation;
    private bool _manualRts;
    private SerialControlLineStatus _status;
    private long _lastObservedTimestamp = Stopwatch.GetTimestamp();
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal Task Worker { get; private set; } = Task.CompletedTask;
    internal SerialControlLineStatus Status
    {
        get
        {
            var status = Volatile.Read(ref _status);
            return _port is not null && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastObservedTimestamp)) > TimeSpan.FromSeconds(2)
                ? status with { Dtr = SerialControlLineMode.Unknown, Rts = SerialControlLineMode.Unknown,
                    InputStatus = SerialModemStatusKind.Error, Cts = null, Dsr = null, Dcd = null, Ri = null,
                    Error = "The serial driver's control-line status query is taking too long." }
                : status;
        }
    }

    internal SerialControlLineRuntime(ISerialControlLinePort? port, long generation,
        SerialSettings settings, CancellationToken lifetime, Action changed)
    {
        _port = port;
        _generation = generation;
        _changed = changed;
        _lifetime = lifetime;
        _manualRts = settings.RtsEnable;
        _status = new(generation,
            SerialControlLinePolicy.UsesDtrHandshake(settings.Handshake) ? SerialControlLineMode.Handshake :
                settings.DtrEnable ? SerialControlLineMode.On : SerialControlLineMode.Off,
            SerialControlLinePolicy.UsesRtsHandshake(settings.Handshake) ? SerialControlLineMode.Handshake :
                settings.RtsToggleOnTransmit ? SerialControlLineMode.Toggle :
                settings.RtsEnable ? SerialControlLineMode.On : SerialControlLineMode.Off,
            port is null ? SerialModemStatusKind.Mock : SerialModemStatusKind.Error,
            Error: port is null ? null : "Waiting for the serial driver's control-line status.");
        if (port is not null)
            _status = _status with { Dtr = SerialControlLineMode.Unknown, Rts = SerialControlLineMode.Unknown };
    }

    internal void Start() => Worker = Task.Run(ObserveAsync, CancellationToken.None);

    private async Task ObserveAsync()
    {
        try
        {
            while (true)
            {
                await Gate.WaitAsync(_lifetime).ConfigureAwait(false);
                try { Refresh(); }
                finally { Gate.Release(); }
                await Task.Delay(250, _lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Publish(Status with { Dtr = SerialControlLineMode.Unknown, Rts = SerialControlLineMode.Unknown,
                InputStatus = SerialModemStatusKind.Error, Cts = null, Dsr = null, Dcd = null, Ri = null, Error = ex.Message });
        }
    }

    private void Refresh()
    {
        if (_port is null) return; // MOCK never fabricates physical input signals.
        var status = new SerialControlLineStatus(_generation, SerialControlLineMode.Unknown,
            SerialControlLineMode.Unknown, SerialModemStatusKind.Error);
        var errors = new List<string>(2);
        try
        {
            var modes = _port.ReadModes();
            status = status with { Dtr = modes.Dtr, Rts = modes.Rts };
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        try
        {
            var inputs = _port.ReadInputs();
            status = status with { InputStatus = SerialModemStatusKind.Available,
                Cts = inputs.Cts, Dsr = inputs.Dsr, Dcd = inputs.Dcd, Ri = inputs.Ri };
        }
        catch (Exception ex)
        {
            status = status with { InputStatus = ex is NotSupportedException ||
                ex is Win32Exception { NativeErrorCode: 1 or 50 or 120 }
                    ? SerialModemStatusKind.Unsupported : SerialModemStatusKind.Error };
            errors.Add(ex.Message);
        }
        Interlocked.Exchange(ref _lastObservedTimestamp, Stopwatch.GetTimestamp());
        Publish(status with { Error = errors.Count == 0 ? null : string.Join("; ", errors) });
    }

    internal Task<SerialControlLineStatus> SetLineAsync(SerialControlLine line, bool enabled, CancellationToken token) =>
        ChangeAsync(line, enabled ? SerialControlLineMode.On : SerialControlLineMode.Off, false, token);

    internal Task<SerialControlLineStatus> SetRtsToggleAsync(bool enabled, CancellationToken token) =>
        ChangeAsync(SerialControlLine.Rts, enabled ? SerialControlLineMode.Toggle :
            _manualRts ? SerialControlLineMode.On : SerialControlLineMode.Off, true, token);

    internal async Task<SerialPortConfiguration> ConfigureAsync(SerialPortConfiguration configuration, CancellationToken token)
    {
        configuration.Validate();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
        await Gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                var applied = _port?.Configure(configuration) ?? configuration;
                _manualRts = applied.RtsEnable;
                Publish(Status with { Dtr = applied.DtrMode, Rts = applied.RtsMode });
                return applied;
            }, linked.Token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private async Task<SerialControlLineStatus> ChangeAsync(SerialControlLine line,
        SerialControlLineMode requested, bool toggleChange, CancellationToken token)
    {
        if (!Enum.IsDefined(line)) throw new ArgumentOutOfRangeException(nameof(line));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
        await Gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                var modes = _port?.ReadModes() ?? (Status.Dtr, Status.Rts);
                var previous = line == SerialControlLine.Dtr ? modes.Item1 : modes.Item2;
                if (previous == SerialControlLineMode.Handshake ||
                    (!toggleChange && !SerialControlLinePolicy.IsManual(previous)))
                    throw new InvalidOperationException("This output is controlled automatically by the driver, or its mode is unavailable.");
                if (previous == SerialControlLineMode.Unknown)
                    throw new InvalidOperationException("The current output mode could not be verified.");
                if (_port is not null)
                    SerialControlLineUpdate.Apply(line, requested, _port.ReadModes, _port.WriteMode);
                var applied = Status with
                {
                    Dtr = line == SerialControlLine.Dtr ? requested : modes.Item1,
                    Rts = line == SerialControlLine.Rts ? requested : modes.Item2
                };
                if (!toggleChange && line == SerialControlLine.Rts) _manualRts = requested == SerialControlLineMode.On;
                Publish(applied);
                // Successful driver readback is the commit point. A later caller
                // cancellation must not hide an output that has already changed.
                return applied;
            }, linked.Token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private void Publish(SerialControlLineStatus status)
    {
        if (Equals(Status, status)) return;
        Volatile.Write(ref _status, status);
        _changed();
    }
}

internal static class SerialControlLineUpdate
{
    internal static void Apply(SerialControlLine line, SerialControlLineMode requested,
        Func<(SerialControlLineMode Dtr, SerialControlLineMode Rts)> read,
        Action<SerialControlLine, SerialControlLineMode> write)
    {
        var before = read();
        var previous = line == SerialControlLine.Dtr ? before.Dtr : before.Rts;
        if (previous == requested) return;
        try
        {
            write(line, requested);
            if (read() != (line == SerialControlLine.Dtr ? (requested, before.Rts) : (before.Dtr, requested)))
                throw new IOException("The serial driver did not confirm the requested control-line mode.");
        }
        catch (Exception error)
        {
            try
            {
                write(line, previous);
                if (read() != before) throw new IOException("The previous control-line modes could not be restored.");
            }
            catch (Exception restoreError)
            {
                throw new SerialControlLineUpdateException("Control-line update failed and the previous state is unverified.",
                    false, new AggregateException(error, restoreError));
            }
            throw new SerialControlLineUpdateException("Control-line update failed; the previous mode was restored. " + error.Message,
                true, error);
        }
    }
}

internal sealed class SerialControlLineUpdateException(string message, bool stateRestored, Exception inner)
    : IOException(message, inner)
{
    internal bool StateRestored { get; } = stateRestored;
}
