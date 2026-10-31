internal static class BinaryContract
{
    public static readonly int[] Lengths = [0,1,255,256,257,511,512,513,1023,1024];

    public static byte[] Payload(int length) => Enumerable.Range(0,length)
        .Select(i => (byte)(i * 73 + (i >> 8) * 19 + 0x5a)).ToArray();

    public static uint Sequence(int length) => 0x34560000u + (uint)length;

    public static byte[] Request(int length) => SerialLabWire.Encode(2,Sequence(length),Payload(length));

    public static void AssertReply(byte[] received,int length)
    {
        SerialLabWire.AssertBytes(received,SerialLabWire.Encode(0x82,Sequence(length),Payload(length)));
        var frames = SerialLabWire.Decode(received);
        if (frames.Length != 1 || !frames[0].CrcValid || frames[0].Payload.Length != length ||
            received[7] != (byte)length || received[8] != (byte)(length >> 8))
            throw new Exception("Binary length/endian/CRC mismatch at " + length);
    }

    public static int VerifyNative(string directory)
    {
        int checks = 0;
        foreach (int length in Lengths)
        {
            byte[] received = File.ReadAllBytes(Path.Combine(directory,$"binary-{length}.bin"));
            AssertReply(received,length); ++checks;
            bool truncatedRejected = false;
            try { SerialLabWire.Decode(received[..^1]); } catch { truncatedRejected = true; }
            if (!truncatedRejected) throw new Exception("Truncated binary accepted: " + length);
            ++checks;
            byte[] corrupt = (byte[])received.Clone();
            corrupt[corrupt.Length - 1] ^= 1;
            if (SerialLabWire.Decode(corrupt).Single().CrcValid) throw new Exception("Bad CRC accepted: " + length);
            ++checks;
            Console.WriteLine($"PASS native C binary reply: payload={length}, frame={length + 11}, high LEN byte={received[8]:X2}");
        }
        bool encodeRejected = false;
        try { SerialLabWire.Encode(2,1,new byte[1025]); } catch (ArgumentException) { encodeRejected = true; }
        if (!encodeRejected) throw new Exception("1025-byte payload encoder did not reject");
        ++checks;
        byte[] oversized = new byte[1036];
        oversized[0]=0xa5; oversized[1]=0x5a; oversized[2]=2; oversized[7]=1; oversized[8]=4;
        bool decodeRejected = false;
        try { SerialLabWire.Decode(oversized); } catch { decodeRejected = true; }
        if (!decodeRejected) throw new Exception("1025-byte payload decoder did not reject");
        ++checks;
        return checks;
    }
}
