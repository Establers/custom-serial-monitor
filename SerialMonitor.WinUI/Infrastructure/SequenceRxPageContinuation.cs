using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Keep only boundary metadata, not the preceding page's payload. CR/LF are
// single-byte delimiters in every recording encoding (ASCII, UTF-8 and CP949).
internal readonly record struct SequenceRxPageContinuation(long? NextOffset = null,
    long Generation = 0, byte? LastByte = null, bool CrlfPartialWasShown = false,
    DateTimeOffset? LastReceivedAt = null)
{
    public bool AfterCr => LastByte == (byte)'\r';

    public bool CanContinue(SequenceRxSlice slice) => NextOffset is null ||
        (NextOffset == slice.Offset && Generation == slice.Generation);

    public bool SuppressEmptyTerminator(RxLineEndingMode mode) => LastByte.HasValue && (mode switch
    {
        RxLineEndingMode.Cr => LastByte != (byte)'\r',
        RxLineEndingMode.Lf => LastByte != (byte)'\n',
        RxLineEndingMode.Crlf => CrlfPartialWasShown,
        _ => LastByte != (byte)'\r' && LastByte != (byte)'\n'
    });

    public SequenceRxPageContinuation Advance(IReadOnlyList<SequenceRxSlice> slices)
    {
        var state = this;
        foreach (var slice in slices)
        {
            if (slice.Bytes.Length == 0) continue;
            if (!state.CanContinue(slice)) state = default;
            var previous = state.LastByte;
            var partial = state.CrlfPartialWasShown;
            foreach (var value in slice.Bytes)
            {
                if (previous == (byte)'\r' && value == (byte)'\n') partial = false;
                else if (value != (byte)'\r' || previous == (byte)'\r') partial = true;
                previous = value;
            }
            state = new(slice.Offset + slice.Bytes.Length, slice.Generation, previous, partial, slice.ReceivedAt);
        }
        return state;
    }
}
