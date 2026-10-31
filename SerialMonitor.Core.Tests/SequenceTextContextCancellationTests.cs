using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Automation;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceTextContextCancellationTests
{
    [Fact]
    public async Task CancelingPrefixPreparationPropagatesEvenForAnEmptySelectedRange()
    {
        using var stop = new CancellationTokenSource();
        var decoded = 0;
        var encoding = new ObservedUtf8Encoding(() => { if (++decoded == 100) stop.Cancel(); });
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(new string('x', 20000)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutomationSequenceMatcher.EvaluateAsync(
                path, 16384, 16384, new("text", "A"), encoding, false, stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.InRange(decoded, 100, 16384);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelingTheTxBoundaryContextDoesNotBecomeARecordingFault()
    {
        using var stop = new CancellationTokenSource();
        var decoded = 0;
        var encoding = new ObservedUtf8Encoding(() => { if (++decoded == 100) stop.Cancel(); });
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceTextContextCancellationTests), Guid.NewGuid().ToString("N"));
        var tx = new SequenceRunStep(1, "status", "Terminal", "None", 0, null, "737461747573");
        var rx = new SequenceRunStep(2, "A", "Terminal", "None", 0, new("text", "A"), "", "RxCheck");
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store,
                new("Cancel text context", 1, "MOCK", "Utf8", [tx, rx]), encoding, default);
            var bytes = Encoding.UTF8.GetBytes(new string('x', 20000));
            session.Offer(1, bytes, Stopwatch.GetTimestamp());
            await session.BeginAsync(tx, 1, 1, 1, stop.Token);
            session.ArmForSend();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.SentAsync(true, Stopwatch.GetTimestamp(), stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));
            await session.FinishAsync("canceled").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(session.Fault);
            Assert.Equal("canceled", session.Summary.State);
            Assert.Equal(bytes.Length, session.Summary.RxBytes);
            var row = Assert.Single((await store.ReadPageAsync(session.RunId)).Rows);
            Assert.Equal("canceled", row.StepState);
            Assert.Equal("unknown", row.SendStatus);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ObservedUtf8Encoding(Action decoded) : UTF8Encoding
    {
        public override Decoder GetDecoder() => new ObservedDecoder(base.GetDecoder(), decoded);
        private sealed class ObservedDecoder(Decoder inner, Action decoded) : Decoder
        {
            public override int GetCharCount(byte[] bytes, int index, int count) => inner.GetCharCount(bytes, index, count);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
                inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
            public override void Convert(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush,
                out int bytesUsed, out int charsUsed, out bool completed)
            {
                decoded();
                inner.Convert(bytes, chars, flush, out bytesUsed, out charsUsed, out completed);
            }
        }
    }
}
