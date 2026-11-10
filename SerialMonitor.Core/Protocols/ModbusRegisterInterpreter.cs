using System.Buffers.Binary;

namespace SerialMonitor.Core.Protocols;

public enum ModbusValueType { UInt16, Int16, UInt32, Int32, Float32 }

public readonly record struct ModbusInterpretedValue(int RegisterOffset, int RegisterCount, double RawValue, double Value)
{
    public bool ScalingOverflow => double.IsFinite(RawValue) && !double.IsFinite(Value);
}

public sealed record ModbusValueInterpretation(IReadOnlyList<ModbusInterpretedValue> Values, int TrailingRegisters);

// Selected-packet interpretation only. Never infers register addresses or pairs
// requests/responses, and never changes the retained frame or receive pipeline.
public static class ModbusRegisterInterpreter
{
    public static bool CanInterpret(ModbusRtuFrame frame)
    {
        var bytes = frame.Bytes.Span;
        return frame.CrcStatus == ModbusCrcStatus.Valid && frame.Kind == ModbusFrameKind.Response &&
            bytes.Length is >= 7 and <= ModbusRtuFrame.MaximumBytes && bytes[0] is >= 1 and <= 247 &&
            bytes[1] is 3 or 4 or 23 && bytes[2] is >= 2 and <= 250 && bytes[2] % 2 == 0 &&
            bytes.Length == bytes[2] + 5 && ModbusRtuCrc.Compute(bytes) == 0;
    }

    public static ModbusValueInterpretation Interpret(ModbusRtuFrame frame, ModbusValueType type,
        int startRegister = 0, bool swapBytes = false, bool swapWords = false, double scale = 1)
    {
        if (!CanInterpret(frame)) throw new ArgumentException("A CRC-valid register read response is required.", nameof(frame));
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (!double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        var data = frame.Bytes.Span.Slice(3, frame.Bytes.Span[2]);
        var registers = data.Length / 2;
        if (startRegister < 0 || startRegister >= registers) throw new ArgumentOutOfRangeException(nameof(startRegister));
        var width = type is ModbusValueType.UInt16 or ModbusValueType.Int16 ? 1 : 2;
        var count = (registers - startRegister) / width;
        var values = new ModbusInterpretedValue[count];
        for (var index = 0; index < count; index++)
        {
            var offset = startRegister + index * width;
            var first = ReadWord(data.Slice(offset * 2, 2), swapBytes);
            double raw;
            if (width == 1) raw = type == ModbusValueType.Int16 ? unchecked((short)first) : first;
            else
            {
                var second = ReadWord(data.Slice((offset + 1) * 2, 2), swapBytes);
                var bits = swapWords ? ((uint)second << 16) | first : ((uint)first << 16) | second;
                raw = type switch
                {
                    ModbusValueType.UInt32 => (double)bits,
                    ModbusValueType.Int32 => (double)unchecked((int)bits),
                    _ => (double)BitConverter.Int32BitsToSingle(unchecked((int)bits))
                };
            }
            values[index] = new(offset, width, raw, raw * scale);
        }
        return new(values, (registers - startRegister) % width);
    }

    private static ushort ReadWord(ReadOnlySpan<byte> bytes, bool swapBytes) => swapBytes
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
}
