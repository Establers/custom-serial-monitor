internal sealed class EvidenceBuffer
{
    private readonly object gate = new();
    private readonly List<Wire> wire = [];
    private string phase = "connect";
    private bool stopped, overflow;

    public void SetPhase(string value) { lock (gate) phase = value; }

    // Queue admission and overflow accounting share the same shutdown boundary.
    public void Record(string direction, byte[] bytes, long timestamp, Func<bool>? enqueue = null)
    {
        lock (gate)
        {
            if (stopped) return;
            if (wire.Count == 512) overflow = true;
            else wire.Add(new(phase, direction, timestamp, Convert.ToHexString(bytes)));
            if (enqueue is not null && !enqueue()) overflow = true;
        }
    }

    public string Received(string name)
    {
        lock (gate) return string.Concat(wire.Where(w => w.Phase == name && w.Direction == "rx").Select(w => w.Hex));
    }

    public EvidenceSnapshot Seal()
    {
        lock (gate)
        {
            stopped = true; // Also excludes callbacks already captured by the event publisher.
            return new(wire.ToArray(), overflow);
        }
    }
}

internal static class EvidenceFinalizer
{
    public static async Task<FinalEvidence> FinishAsync(EvidenceBuffer evidence, string? failure,
        CancellationTokenSource cancellation, Task? drain, Func<Task> close, Action unsubscribe, Func<long> readErrors)
    {
        void Failed(string stage, Exception ex) => failure = Append(failure, $"{stage}: {ex}");
        try { cancellation.Cancel(); }
        catch (Exception ex) { Failed("Cancel", ex); }
        try { if (drain is not null) await drain; }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token && cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Failed("Drain", ex); }
        try { await close(); }
        catch (Exception ex) { Failed("Close", ex); }
        try { unsubscribe(); }
        catch (Exception ex) { Failed("Unsubscribe", ex); }
        var snapshot = evidence.Seal();
        long? serialErrors = null;
        try { serialErrors = readErrors(); }
        catch (Exception ex) { Failed("Read serial errors", ex); }
        if (snapshot.Overflow) failure = Append(failure, "Evidence overflow.");
        if (serialErrors is > 0) failure = Append(failure, $"Serial driver errors: {serialErrors}.");
        return new(snapshot.Wire, snapshot.Overflow, serialErrors, failure);
    }

    private static string Append(string? previous, string next) => previous is null ? next : previous + Environment.NewLine + next;
}

internal sealed record Wire(string Phase, string Direction, long Timestamp, string Hex);
internal sealed record EvidenceSnapshot(Wire[] Wire, bool Overflow);
internal sealed record FinalEvidence(Wire[] Wire, bool Overflow, long? SerialErrors, string? Failure)
{
    public bool Passed => Failure is null;
}
