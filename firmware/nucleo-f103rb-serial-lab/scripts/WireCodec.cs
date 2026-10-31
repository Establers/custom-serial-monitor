using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

// Shared only by PC tools; the firmware has a separate C implementation.
public static class SerialLabWire
{
    public const int MaxPayloadLength = 1024;
    public sealed class Frame
    {
        public byte Type;
        public uint Sequence;
        public byte[] Payload = new byte[0];
        public bool CrcValid;
    }

    public static ushort Crc16(byte[] bytes, int offset, int length)
    {
        ushort crc = 0xffff;
        for (int i = offset; i < offset + length; ++i)
        {
            crc ^= (ushort)(bytes[i] << 8);
            for (int bit = 0; bit < 8; ++bit)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    public static byte[] Encode(byte type, uint sequence, byte[] payload)
    {
        if (payload == null || payload.Length > MaxPayloadLength) throw new ArgumentException("Payload limit: 1024 bytes");
        byte[] bytes = new byte[payload.Length + 11];
        bytes[0] = 0xa5; bytes[1] = 0x5a; bytes[2] = type;
        for (int i = 0; i < 4; ++i) bytes[3 + i] = (byte)(sequence >> (i * 8));
        bytes[7] = (byte)payload.Length; bytes[8] = (byte)(payload.Length >> 8);
        Array.Copy(payload, 0, bytes, 9, payload.Length);
        ushort crc = Crc16(bytes, 2, payload.Length + 7);
        bytes[bytes.Length - 2] = (byte)crc; bytes[bytes.Length - 1] = (byte)(crc >> 8);
        return bytes;
    }

    public static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace('-', ' '); }

    public static Frame[] Decode(byte[] bytes)
    {
        var result = new List<Frame>();
        for (int i = 0; i < bytes.Length; )
        {
            // ASCII acknowledgements may precede generated HEX packets.
            if (bytes[i] != 0xa5) { ++i; continue; }
            if (i + 1 >= bytes.Length || bytes[i + 1] != 0x5a) throw new Exception("Broken frame header");
            if (i + 11 > bytes.Length) throw new Exception("Truncated frame header");
            int length = bytes[i + 7] | bytes[i + 8] << 8;
            if (length > MaxPayloadLength || i + length + 11 > bytes.Length) throw new Exception("Invalid/truncated frame length");
            var frame = new Frame();
            frame.Type = bytes[i + 2];
            frame.Sequence = (uint)(bytes[i + 3] | bytes[i + 4] << 8 | bytes[i + 5] << 16 | bytes[i + 6] << 24);
            frame.Payload = new byte[length];
            Array.Copy(bytes, i + 9, frame.Payload, 0, length);
            ushort expected = (ushort)(bytes[i + length + 9] | bytes[i + length + 10] << 8);
            frame.CrcValid = Crc16(bytes, i + 2, length + 7) == expected;
            result.Add(frame); i += length + 11;
        }
        return result.ToArray();
    }

    public static void AssertBytes(byte[] actual, byte[] expected)
    {
        if (actual.Length != expected.Length) throw new Exception("Byte length: expected " + expected.Length + ", actual " + actual.Length);
        for (int i = 0; i < actual.Length; ++i)
            if (actual[i] != expected[i]) throw new Exception("Byte mismatch at " + i);
    }

    public static void AssertTextRecords(byte[] bytes, string prefix, int count, int dropEvery, int duplicateEvery)
    {
        string text = Encoding.UTF8.GetString(bytes);
        MatchCollection matches = Regex.Matches(text, "^" + Regex.Escape(prefix) + @" seq=(\d+) [^\r\n]*\r$", RegexOptions.Multiline);
        int index = 0;
        for (int seq = 1; seq <= count; ++seq)
        {
            if (dropEvery > 0 && seq % dropEvery == 0) continue;
            int repeats = duplicateEvery > 0 && seq % duplicateEvery == 0 ? 2 : 1;
            for (int j = 0; j < repeats; ++j)
            {
                if (index >= matches.Count || int.Parse(matches[index].Groups[1].Value) != seq)
                    throw new Exception("Sequence mismatch at record " + index + "; expected " + seq);
                ++index;
            }
        }
        if (index != matches.Count) throw new Exception("Unexpected extra records: " + (matches.Count - index));
        // Incomplete or malformed records must not disappear from the check.
        int starts = Regex.Matches(text, "^" + Regex.Escape(prefix) + " ", RegexOptions.Multiline).Count;
        if (starts != matches.Count) throw new Exception("Malformed or unterminated text record");
    }

    public static void AssertGeneratedFrames(byte[] bytes, int count, int corruptEvery)
    {
        Frame[] frames = Decode(bytes);
        if (frames.Length != count) throw new Exception("Expected " + count + " frames, got " + frames.Length);
        for (int i = 0; i < frames.Length; ++i)
        {
            Frame frame = frames[i];
            uint seq = (uint)i + 1;
            if (frame.Type != 0x90 || frame.Sequence != seq || frame.Payload.Length != 32) throw new Exception("Invalid generated frame " + seq);
            bool corrupt = corruptEvery > 0 && seq % corruptEvery == 0;
            if (frame.CrcValid == corrupt) throw new Exception("Unexpected CRC result at sequence " + seq);
            for (int j = 0; j < 32; ++j)
                if (frame.Payload[j] != (byte)(seq + j)) throw new Exception("Payload mismatch at sequence " + seq);
        }
    }

    public static void AssertBurstPayload(byte[] bytes, int padding)
    {
        var expected = new StringBuilder();
        for (int i = 0; i < padding; ++i) expected.Append((char)('A' + i % 26));
        foreach (Match record in Regex.Matches(Encoding.UTF8.GetString(bytes), @"^BURST seq=\d+ payload=([^\r\n]*)\r$", RegexOptions.Multiline))
            if (record.Groups[1].Value != expected.ToString()) throw new Exception("Corrupted BURST payload");
    }
}
