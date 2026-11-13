using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using static Oracle;

internal sealed partial class LiveCheck
{
    private CommandSequence? _guiSequence;
    private string _guiHash = "";

    private async Task LoadGuiSequence()
    {
        _guiHash = Hash(sequencePath);
        var loaded = await new CommandSequenceFileService().LoadAsync([Path.GetFullPath(sequencePath)], token);
        Require(loaded.Count == 1, "Expected one exact GUI sequence.");
        var sequence = loaded[0];
        Require(sequence.RepeatCount == 1 && sequence.Steps.Count == 52 && sequence.Steps.All(step => step.IsEnabled) &&
            sequence.Steps.Count(step => step.Kind == CommandSequenceStepKind.Tx) == 26 &&
            sequence.Steps.Count(step => step.Kind == CommandSequenceStepKind.RxCheck) == 26,
            "Exact GUI recipe requires 52 enabled steps, 26 TX and 26 RX Check, repeat one.");
        for (var index = 0; index < sequence.Steps.Count; index++)
        {
            var step = sequence.Steps[index];
            Require(step.StepNumber == index + 1 && step.Kind == (index % 2 == 0 ? CommandSequenceStepKind.Tx : CommandSequenceStepKind.RxCheck), "GUI TX/RX Check order or numbering changed.");
            Require(step.TxMode is TxSendMode.Hex or TxSendMode.Terminal && step.ResponseCheck is null,
                "Recipe must specify each mode explicitly and use separate RX Check steps.");
            Require(step.DelayAfterMs == (step.Kind == CommandSequenceStepKind.RxCheck ? 700 : 0), "GUI whole-window wait contract changed.");
        }
        Require(Hash(sequencePath) == _guiHash, "GUI file changed during import.");
        _guiSequence = sequence;
        await File.WriteAllBytesAsync(Path.Combine(evidence.Directory, "sequence-source.gui.json"), await File.ReadAllBytesAsync(sequencePath, token), token);
    }

    private async Task ReplayGuiSequence(int round)
    {
        var sequence = _guiSequence ?? throw new InvalidOperationException("GUI sequence was not loaded.");
        Require(Hash(sequencePath) == _guiHash && _serial.IsConnected && _serial.ExclusiveSessionOwner is null,
            "Sequence source changed or transport is not idle.");
        var generation = _serial.ReceiveSessionGeneration;
        var definition = new SequenceRunDefinition(sequence.Name, sequence.RepeatCount, port, "Utf8", sequence.Steps.Select(step =>
        {
            var mode = step.TxMode ?? TxSendMode.Hex;
            if (step.Kind == CommandSequenceStepKind.RxCheck)
                return new SequenceRunStep(step.StepNumber, step.CommandText, mode.ToString(), "None", step.DelayAfterMs,
                    new ResponseCheck(mode == TxSendMode.Hex ? "hex" : "text", step.CommandText), "", "RxCheck");
            var ending = step.LineEndingMode ?? TxLineEndingMode.Crlf;
            return new SequenceRunStep(step.StepNumber, step.CommandText, mode.ToString(), ending.ToString(), step.DelayAfterMs,
                step.ResponseCheck, Convert.ToHexString(TxPayloadEncoder.Encode(step.CommandText, mode, ending)));
        }).ToArray());
        var store = new SequenceRunStore(Path.Combine(evidence.Directory, "sequence-runs"));
        await using var session = await SequenceRunSession.StartAsync(store, definition, Encoding.UTF8, token);
        using var runStop = CancellationTokenSource.CreateLinkedTokenSource(token, session.FailureToken);
        var connectionGate = new object();
        var attached = true;
        var serialErrors = SerialErrors;
        void PublishConnection()
        {
            var errors = SerialErrors;
            var nextGeneration = _serial.ReceiveSessionGeneration;
            session.ConnectionChanged(nextGeneration, _serial.IsConnected, errors > serialErrors);
            serialErrors = Math.Max(serialErrors, errors);
        }
        void Receive(long observedGeneration, ReceivedByteChunk chunk)
        {
            lock (connectionGate)
            {
                if (!attached) return;
                PublishConnection();
                if (observedGeneration == _serial.ReceiveSessionGeneration)
                    session.Offer(observedGeneration, chunk.Bytes, chunk.ReceivedTimestamp);
            }
        }
        void Status(object? sender, EventArgs eventArgs) { lock (connectionGate) if (attached) PublishConnection(); }
        _serial.CaptureBytesReceived += Receive; _serial.StatusChanged += Status;
        lock (connectionGate) PublishConnection();
        var oracle = new GuiWireOracle();
        var lastTxMark = -1;
        byte[]? lastTx = null;
        TxSendMode lastMode = TxSendMode.Hex;
        var checkedRows = 0;
        var completed = 0;
        var begin = evidence.Entries(_case).Length;
        var writtenAtStart = _serial.WrittenByteCount;
        var started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            // This is the exact production observer source, linked by the project;
            // its full-window RX checks use the actual core matcher/store.
            var actual = new SequenceRunObserver(session, definition, () => _serial.ReceiveSessionGeneration);
            var observer = new CheckedSequenceObserver(actual, (position, step) =>
            {
                if (step.Kind != CommandSequenceStepKind.RxCheck) return;
                Require(lastTx is not null && lastTxMark >= 0, "RX Check lost its preceding TX.");
                var raw = evidence.Entries(_case).Skip(lastTxMark).Where(entry => entry.Direction == "RX").SelectMany(entry => entry.Bytes).ToArray();
                oracle.Verify(lastTx!, lastMode, raw);
                checkedRows++;
                Require(session.Summary.Matched == checkedRows && session.Summary.NotMatched == 0 && session.Summary.Inconclusive == 0,
                    "Actual core RX Check failed at step " + (position + 1));
            });
            await new CommandSequenceRunner().RunAsync(sequence,
                _ => Task.FromResult(_serial.IsConnected && _serial.ReceiveSessionGeneration == generation),
                async (step, cancellation) =>
                {
                    lastMode = step.TxMode ?? TxSendMode.Hex;
                    lastTx = TxPayloadEncoder.Encode(step.CommandText, lastMode, step.LineEndingMode ?? TxLineEndingMode.Crlf);
                    lastTxMark = evidence.Entries(_case).Length;
                    evidence.Tx(lastTx, generation);
                    await _serial.SendBytesAsync(lastTx, Convert.ToHexString(lastTx), cancellation);
                    return true;
                }, () => false, _ => { }, value => completed = value, runStop.Token, observer: observer);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            lock (connectionGate) attached = false;
            _serial.CaptureBytesReceived -= Receive; _serial.StatusChanged -= Status;
            try { await session.FinishAsync(failure is null ? "completed" : "failed", failure?.ToString()); }
            catch (Exception cleanup) when (failure is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence.Directory, $"gui-round-{round}-cleanup-error.txt"), cleanup.ToString());
            }
        }
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var page = await store.ReadPageAsync(session.RunId, token: token);
        var rows = page.Rows;
        var summary = session.Summary;
        var entries = evidence.Entries(_case).Skip(begin).ToArray();
        var tx = entries.Where(entry => entry.Direction == "TX").ToArray();
        var rx = entries.Where(entry => entry.Direction == "RX").ToArray();
        Require(!page.HasMore && rows.Count == 52 && completed == 52 && checkedRows == 26 && tx.Length == 26 &&
            summary.State == "completed" && summary.Sent == 26 && summary.Matched == 26 && summary.Attempts == 52 &&
            summary.NotMatched == 0 && summary.Inconclusive == 0 && summary.CompletedRepeats == 1 && summary.MatchedRepeats == 1,
            "Exact GUI run counts/actual core summary mismatch.");
        Require(rows.All(row => row.Attempt == 1 && row.StepState == "completed" && row.Error is null), "Sequence retried or stored an incomplete/error step.");
        Require(rows.Where(row => row.StepKind == "RxCheck").All(row => row.CheckStatus == "matched" &&
            (row.EndedAt - row.StartedAt).TotalMilliseconds >= row.TimeoutMs - 2), "RX Check returned before its whole observation window.");
        Require(elapsed >= 18200 && _serial.WrittenByteCount - writtenAtStart == tx.Sum(entry => entry.Bytes.Length) &&
            oracle.CorruptReplies == 1 && oracle.FinalResetAndQuiet && summary.RxBytes == rx.Sum(entry => entry.Bytes.Length),
            "Sequence duration, independent wire count, deliberate CRC case or final reset/RTU state mismatch.");
        await Task.Delay(200, token);
        Require(evidence.Entries(_case).Skip(begin).Where(entry => entry.Direction == "RX").Sum(entry => entry.Bytes.Length) == summary.RxBytes,
            "Unexpected output after final quiet RTU acknowledgement.");
        evidence.RequireComplete();
        // Keep post-run probes out of the exact recipe's TX/RX and result store.
        // ID? escapes RTU without clearing pending/fault/RAM state, unlike LAB.
        var recipeCase = _case;
        var auditCase = recipeCase + "-final-state";
        string finalStatus;
        _case = auditCase;
        evidence.Select(auditCase);
        try
        {
            await Text("ID?", "ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1\r\n");
            finalStatus = await TextValue("RTU STATUS?");
            Require(finalStatus == "RTU reads=0 writes=0 led=0 fault=NONE delay_ms=0 pending=0\r\n",
                "GUI recipe did not leave reset counters/LED/fault/pending state: " + finalStatus);
            await Text("RTU", "OK RTU\r\n");
        }
        finally { _case = recipeCase; evidence.Select(recipeCase); }
        await File.WriteAllTextAsync(Path.Combine(evidence.Directory, $"gui-round-{round}.json"), JsonSerializer.Serialize(new
        {
            round, sequenceFile = Path.GetFullPath(sequencePath), sequenceSha256 = _guiHash,
            engine = "CommandSequenceRunner + linked production SequenceRunObserver + actual SequenceRunSession/RX matcher",
            summary, completedSteps = completed, txCount = tx.Length, rxCheckCount = checkedRows, elapsedMilliseconds = elapsed,
            configuredWholeWindowMilliseconds = 18200, independentIntentionalCrcErrors = oracle.CorruptReplies,
            finalState = "Final reset + quiet acknowledgement and 200ms passive quiet; separate three-command ID?/RTU STATUS?/RTU audit verifies reset counters/LED/fault/pending without first clearing them.",
            finalStatus, finalStateEvidenceCase = auditCase,
            steps = rows.Select(row => new { row.Step, row.StepKind, row.SendStatus, row.CheckStatus, row.Attempt,
                row.TimeoutMs, row.StartedAt, row.EndedAt, elapsedMilliseconds = (row.EndedAt - row.StartedAt).TotalMilliseconds,
                row.ResponseMs, row.MatchStart, row.MatchLength, row.RelatedTxStep, row.Error })
        }, new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine($"  GUI round {round}: TX26 / RX Check26 matched / steps52 / elapsed={elapsed:F0}ms / final=reset+quiet");
    }

    private long SerialErrors => _serial.SerialFrameErrorCount + _serial.SerialParityErrorCount + _serial.SerialOverrunErrorCount + _serial.SerialRxOverErrorCount;
}

internal sealed class CheckedSequenceObserver(ICommandSequenceObserver actual, Action<int, CommandSequenceStep> checkedStep) : ICommandSequenceObserver
{
    private int _position;
    private CommandSequenceStep? _step;
    public Task BeforeSendAsync(int position, CommandSequenceStep step, CancellationToken token)
    { _position = position; _step = step; return actual.BeforeSendAsync(position, step, token); }
    public Task CheckReceiveAsync(int position, CommandSequenceStep step, CancellationToken token)
    { _position = position; _step = step; return actual.CheckReceiveAsync(position, step, token); }
    public Task AfterSendAsync(bool sent, CancellationToken token) => actual.AfterSendAsync(sent, token);
    public async Task AfterStepAsync(CancellationToken token)
    { await actual.AfterStepAsync(token); checkedStep(_position, _step!); }
}

// Stateful oracle is independent of RX patterns and the application's decoder.
// Only the fixture's physical button bit is dynamic; its CRC/padding and LED bit
// are still checked over the complete observed response.
internal sealed class GuiWireOracle
{
    private readonly ushort[] _holding = new ushort[128];
    private readonly ushort[] _coils = new ushort[2048];
    private bool _reset, _crcArmed;
    private string _lastText = "";
    public int CorruptReplies { get; private set; }
    public bool FinalResetAndQuiet { get; private set; }

    public void Verify(byte[] tx, TxSendMode mode, byte[] actual)
    {
        byte[] expected;
        if (mode == TxSendMode.Terminal)
        {
            var text = Encoding.UTF8.GetString(tx);
            expected = Encoding.ASCII.GetBytes(text switch
            {
                "LAB\r\n" => "OK LAB\r\n", "RTU RESET\r\n" => "OK RTU RESET\r\n",
                "RTU FAULT CRC\r\n" => "OK RTU FAULT CRC\r\n", "RTU\r\n" => "OK RTU\r\n",
                _ => throw new InvalidOperationException("Unsupported GUI fixture command or line ending: " + text)
            });
            if (text == "LAB\r\n") _crcArmed = false;
            if (text == "RTU RESET\r\n")
            {
                Array.Clear(_holding); Array.Clear(_coils); _holding[4] = 0xFF85; _holding[5] = 250;
                _holding[6] = 0x1234; _holding[7] = 0x5678; _holding[8] = 0x41C8;
                _reset = true; _crcArmed = false;
            }
            if (text == "RTU FAULT CRC\r\n") _crcArmed = true;
            FinalResetAndQuiet = text == "RTU\r\n" && _lastText == "RTU RESET\r\n" && _reset && _coils.All(value => value == 0);
            _lastText = text;
        }
        else
        {
            Require(_reset && tx.Length >= 8 && tx[0] == 1 && Crc(tx) == 0, "GUI request is not an independently valid unit1 RTU request.");
            FinalResetAndQuiet = false; _lastText = "";
            var function = tx[1];
            var address = BinaryPrimitives.ReadUInt16BigEndian(tx.AsSpan(2));
            var count = BinaryPrimitives.ReadUInt16BigEndian(tx.AsSpan(4));
            if (function is >= 1 and <= 4)
            {
                Require(tx.Length == 8 && count > 0 && count <= (function <= 2 ? 2000 : 125), "GUI read request extent.");
                if (address == 0x2000) expected = Adu(1, (byte)(function | 128), 2);
                else
                {
                    var values = new ushort[count];
                    for (var index = 0; index < count; index++)
                    {
                        var position = address + index;
                        if (function is 3 or 4 && position < 2) values[index] = position == 0 ? (ushort)250 : (ushort)100;
                        else
                        {
                            var offset = position - 0x1000;
                            Require(offset >= 0 && offset < (function <= 2 ? 2048 : 128), "GUI request leaves fixture bank.");
                            values[index] = function switch
                            {
                                1 => _coils[offset],
                                2 when offset == 0 => actual.Length == 6 ? (ushort)(actual[3] & 1) : throw new InvalidOperationException("Dynamic input extent."),
                                2 when offset == 1 => _coils[0], 2 => 0,
                                3 => _holding[offset],
                                4 => offset switch { 7 => _coils[0], 8 => 0xFF85, 9 => 250, 10 => 0x1234, 11 => 0x5678, 12 => 0x41C8, 13 => 0,
                                    _ => throw new InvalidOperationException("GUI oracle does not guess dynamic input counters.") },
                                _ => throw new InvalidOperationException("Unexpected read function.")
                            };
                        }
                    }
                    expected = Reply(function, values);
                }
            }
            else
            {
                Require(function is 5 or 6 or 15 or 16, "Unsupported GUI write function.");
                var start = address - 0x1000;
                if (function is 5 or 6)
                {
                    Require(tx.Length == 8 && start >= 0 && start < (function == 5 ? 2048 : 128), "GUI single-write extent.");
                    if (function == 5) { Require(count is 0 or 0xFF00, "Coil encoding."); _coils[start] = count == 0 ? (ushort)0 : (ushort)1; }
                    else _holding[start] = count;
                }
                else
                {
                    var bytes = function == 15 ? (count + 7) / 8 : count * 2;
                    Require(count > 0 && start >= 0 && start + count <= (function == 15 ? 2048 : 128) && tx.Length == bytes + 9 && tx[6] == bytes,
                        "GUI multiple-write extent/count.");
                    for (var index = 0; index < count; index++)
                        if (function == 15) _coils[start + index] = (ushort)((tx[7 + index / 8] >> (index % 8)) & 1);
                        else _holding[start + index] = BinaryPrimitives.ReadUInt16BigEndian(tx.AsSpan(7 + index * 2));
                }
                expected = Ack(tx);
            }
            if (_crcArmed) { expected[^1] ^= 1; _crcArmed = false; CorruptReplies++; }
        }
        Require(actual.AsSpan().SequenceEqual(expected), "Full GUI-step response differs from independent oracle: expected=" + Convert.ToHexString(expected) + " actual=" + Convert.ToHexString(actual));
    }
}
