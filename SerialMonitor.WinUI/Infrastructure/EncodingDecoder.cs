using System.Text;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public readonly record struct DecodeResult(string Text, bool HadError);

public sealed class EncodingDecoder
{
    public EncodingDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public DecodeResult Decode(ReadOnlySpan<byte> bytes, RxEncodingMode mode)
    {
        if (mode == RxEncodingMode.Hex)
        {
            return new DecodeResult(Convert.ToHexString(bytes), HadError: false);
        }

        try
        {
            return new DecodeResult(CreateEncoding(mode, new DecoderExceptionFallback()).GetString(bytes), HadError: false);
        }
        catch (DecoderFallbackException)
        {
            return new DecodeResult(CreateEncoding(mode, DecoderFallback.ReplacementFallback).GetString(bytes), HadError: true);
        }
    }

    private static Encoding CreateEncoding(RxEncodingMode mode, DecoderFallback decoderFallback)
    {
        var encoding = mode switch
        {
            RxEncodingMode.Ascii => Encoding.ASCII,
            RxEncodingMode.Cp949 => Encoding.GetEncoding(949),
            _ => Encoding.UTF8
        };

        var clone = (Encoding)encoding.Clone();
        clone.DecoderFallback = decoderFallback;
        return clone;
    }
}
