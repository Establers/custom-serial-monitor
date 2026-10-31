using System.Diagnostics;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal sealed class SequenceRunObserver(SequenceRunSession session, SequenceRunDefinition definition,
    Func<long> generation, Action<string>? phase = null) : ICommandSequenceObserver
{
    private int _lastPosition = -1, _attempt;
    private bool _checks;
    public async Task CheckReceiveAsync(int position, CommandSequenceStep step, CancellationToken token)
    {
        _lastPosition = position; _attempt = 1;
        phase?.Invoke("RX Check");
        await session.BeginAsync(definition.Steps[position % definition.Steps.Count],
            position / definition.Steps.Count + 1, 1, generation(), token);
        await session.CheckFullWindowAsync(token);
    }
    public async Task BeforeSendAsync(int position, CommandSequenceStep step, CancellationToken token)
    {
        _attempt = _lastPosition == position ? _attempt + 1 : 1;
        _lastPosition = position;
        _checks = step.ResponseCheck is not null;
        await session.BeginAsync(definition.Steps[position % definition.Steps.Count],
            position / definition.Steps.Count + 1, _attempt, generation(), token);
        token.ThrowIfCancellationRequested();
        session.ArmForSend();
    }
    public async Task AfterSendAsync(bool sent, CancellationToken token)
    {
        if (sent && _checks) phase?.Invoke("Checking response");
        await session.SentAsync(sent, Stopwatch.GetTimestamp(), token);
        if (sent) phase?.Invoke("Delay");
    }
    public Task AfterStepAsync(CancellationToken token) => session.EndStepAsync(token);
}
