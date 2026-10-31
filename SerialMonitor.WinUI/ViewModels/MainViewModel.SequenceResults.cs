using System.Text;
using SerialMonitor.Core.Automation;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private SequenceRunSession? _sequenceRunSession;
    private readonly object _sequenceResultGate = new();
    private long _sequenceResultGeneration;
    private long _sequenceResultSerialErrors;
    public SequenceRunStore SequenceResultsStore { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "sequence-runs"));

    private SequenceRunDefinition CreateSequenceRunDefinition(CommandSequence sequence, TxSendMode defaultMode)
    {
        var steps = sequence.Steps.Select(step =>
        {
            var mode = step.TxMode ?? defaultMode;
            if (step.Kind == CommandSequenceStepKind.RxCheck)
                return new SequenceRunStep(step.StepNumber, step.CommandText, mode.ToString(), "None", step.DelayAfterMs,
                    new ResponseCheck(mode == TxSendMode.Hex ? "hex" : "text", step.CommandText), "", "RxCheck");
            var text = step.TxMode.HasValue ? step.CommandText : step.CommandText.Trim();
            byte[] bytes;
            if (mode == TxSendMode.Hex)
            {
                if (!HexPayloadParser.TryParse(text, out bytes, out var error)) throw new ArgumentException(error);
            }
            else bytes = TxPayloadEncoder.Encode(text, TxSendMode.Terminal, step.LineEndingMode ?? SelectedTxLineEnding);
            return new SequenceRunStep(step.StepNumber, text, mode.ToString(),
                (step.LineEndingMode ?? SelectedTxLineEnding).ToString(), step.DelayAfterMs,
                step.ResponseCheck, Convert.ToHexString(bytes));
        }).ToArray();
        return new(sequence.Name, sequence.RepeatCount, _currentSerialSettings.PortName,
            _currentSerialSettings.Encoding.ToString(), steps);
    }

    private Encoding SequenceResponseEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(_currentSerialSettings.Encoding.ToString() switch
        { "Ascii" => "us-ascii", "Cp949" => "ks_c_5601-1987", _ => "utf-8" });
    }

    private void OnSequenceResultBytes(long generation, ReceivedByteChunk chunk)
    {
        var session = Volatile.Read(ref _sequenceRunSession);
        if (session is null) return;
        var snapshot = CaptureConnectionSnapshot;
        lock (_sequenceResultGate)
        {
            if (!ReferenceEquals(session, _sequenceRunSession)) return;
            var liveGeneration = _serialService.ReceiveSessionGeneration;
            if (snapshot.Generation == liveGeneration) PublishSequenceConnection(session, snapshot, false);
            // An already-dispatched callback from an old connection must not
            // invalidate a check on the replacement connection.
            if (generation == liveGeneration) session.Offer(generation, chunk.Bytes, chunk.ReceivedTimestamp);
        }
    }

    private void OnSequenceResultConnection(object? sender, EventArgs args)
    {
        var session = Volatile.Read(ref _sequenceRunSession);
        if (session is null) return;
        var snapshot = CaptureConnectionSnapshot;
        lock (_sequenceResultGate)
        {
            if (!ReferenceEquals(session, _sequenceRunSession) || snapshot.Generation != _serialService.ReceiveSessionGeneration) return;
            PublishSequenceConnection(session, snapshot, true);
        }
    }

    // Called under _sequenceResultGate. Snapshot reads can finish out of order:
    // errors are monotonic within one generation, and reset only for a new one.
    private void PublishSequenceConnection(SequenceRunSession session, CaptureConnection snapshot, bool statusChanged)
    {
        if (snapshot.Generation < _sequenceResultGeneration || snapshot.Generation != _serialService.ReceiveSessionGeneration) return;
        // A delayed status snapshot must not resurrect a connection already closed.
        var connected = _serialService.IsConnected;
        if (snapshot.Generation != _serialService.ReceiveSessionGeneration) return;
        var newGeneration = snapshot.Generation != _sequenceResultGeneration;
        var newSerialError = newGeneration ? snapshot.SerialErrorCount > 0 : snapshot.SerialErrorCount > _sequenceResultSerialErrors;
        if (newGeneration) { _sequenceResultGeneration = snapshot.Generation; _sequenceResultSerialErrors = snapshot.SerialErrorCount; }
        else if (newSerialError) _sequenceResultSerialErrors = snapshot.SerialErrorCount;
        if (statusChanged || newGeneration || newSerialError)
            session.ConnectionChanged(snapshot.Generation, connected, newSerialError);
    }

    private void AttachSequenceRecording(SequenceRunSession session)
    {
        lock (_sequenceResultGate)
        {
            var baseline = CaptureConnectionSnapshot;
            _sequenceResultGeneration = baseline.Generation; _sequenceResultSerialErrors = baseline.SerialErrorCount;
            Volatile.Write(ref _sequenceRunSession, session);
            _serialService.CaptureBytesReceived += OnSequenceResultBytes;
            _serialService.StatusChanged += OnSequenceResultConnection;
            PublishSequenceConnection(session, CaptureConnectionSnapshot, true);
        }
    }

    private void DetachSequenceRecording()
    {
        lock (_sequenceResultGate)
        {
            Volatile.Write(ref _sequenceRunSession, null);
            _serialService.CaptureBytesReceived -= OnSequenceResultBytes;
            _serialService.StatusChanged -= OnSequenceResultConnection;
        }
    }
}
