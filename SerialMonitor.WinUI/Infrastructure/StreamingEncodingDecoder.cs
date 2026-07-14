using System.Text;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class StreamingEncodingDecoder
{
    private readonly TrackingDecoderFallback _fallback = new();
    private readonly Decoder _decoder;

    public StreamingEncodingDecoder(RxEncodingMode mode)
    {
        var encoding = EncodingDecoder.CreateEncoding(NormalizeTerminalEncoding(mode), _fallback);
        _decoder = encoding.GetDecoder();
    }

    public DecodeResult Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        _fallback.ResetError();
        var chars = new char[Math.Max(4, bytes.Length * 2 + 4)];
        _decoder.Convert(
            bytes,
            chars,
            flush,
            out var bytesUsed,
            out var charsUsed,
            out var completed);

        if (bytesUsed != bytes.Length || !completed)
        {
            throw new InvalidOperationException("Bridge streaming decoder buffer was too small.");
        }

        return new DecodeResult(new string(chars, 0, charsUsed), _fallback.HadError);
    }

    private static RxEncodingMode NormalizeTerminalEncoding(RxEncodingMode mode)
    {
        return mode switch
        {
            RxEncodingMode.Ascii => RxEncodingMode.Ascii,
            RxEncodingMode.Cp949 => RxEncodingMode.Cp949,
            _ => RxEncodingMode.Utf8
        };
    }

    private sealed class TrackingDecoderFallback : DecoderFallback
    {
        private int _hadError;

        public bool HadError => Volatile.Read(ref _hadError) != 0;

        public override int MaxCharCount => 1;

        public override DecoderFallbackBuffer CreateFallbackBuffer() => new TrackingDecoderFallbackBuffer(this);

        public void ResetError() => Volatile.Write(ref _hadError, 0);

        private void RecordError() => Volatile.Write(ref _hadError, 1);

        private sealed class TrackingDecoderFallbackBuffer : DecoderFallbackBuffer
        {
            private readonly TrackingDecoderFallback _owner;
            private int _remaining;

            public TrackingDecoderFallbackBuffer(TrackingDecoderFallback owner)
            {
                _owner = owner;
            }

            public override int Remaining => _remaining;

            public override bool Fallback(byte[] bytesUnknown, int index)
            {
                if (_remaining != 0)
                {
                    return false;
                }

                _owner.RecordError();
                _remaining = 1;
                return true;
            }

            public override char GetNextChar()
            {
                if (_remaining == 0)
                {
                    return '\0';
                }

                _remaining = 0;
                return '?';
            }

            public override bool MovePrevious()
            {
                if (_remaining != 0)
                {
                    return false;
                }

                _remaining = 1;
                return true;
            }

            public override void Reset()
            {
                _remaining = 0;
                base.Reset();
            }
        }
    }
}
