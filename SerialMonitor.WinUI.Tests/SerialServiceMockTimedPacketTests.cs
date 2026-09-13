using System.Buffers.Binary;
using System.Text;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialServiceMockTimedPacketTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisualHexPattern_EmitsSelfDescribingFirstMiddleLastPackets(bool queueNormalSampleBeforeStart)
    {
        await using var service = new SerialService();
        service.ConfigureMockStress(
            linesPerSecond: 10,
            burstSize: 1,
            injectEvents: false,
            injectInvalidBytes: false,
            MockGeneratorPattern.VisualHexPackets);
        await service.ConnectAsync(
            new SerialSettings { PortName = "MOCK" },
            new SerialReceiveOptions(),
            CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (queueNormalSampleBeforeStart)
        {
            // Exercise the transition with a normal sample still queued.
            Assert.True(await service.ReceivedBytes.WaitToReadAsync(timeout.Token));
        }

        service.StartMockStress();
        var observedLengths = new HashSet<int>();
        var currentGroup = -1;
        var expectedPacketIndex = 0;
        var expectedPacketsInGroup = 0;
        for (long expectedSequence = 1; expectedSequence <= 12; expectedSequence++)
        {
            ReceivedByteChunk chunk;
            do
            {
                chunk = await service.ReceivedBytes.ReadAsync(timeout.Token);
                // Starting stress does not discard normal samples already queued
                // or being published by the receiver. Only allow them before HEX starts.
            }
            while (expectedSequence == 1 && Encoding.UTF8.GetString(chunk.Bytes)
                .StartsWith("INFO mock serial sample #", StringComparison.Ordinal));
            var packet = chunk.Bytes;
            observedLengths.Add(packet.Length);

            Assert.InRange(packet.Length, 24, 64);
            Assert.Equal(new byte[] { 0xAA, 0x55 }, packet.AsSpan(0, 2).ToArray());
            Assert.Equal(new byte[] { 0x55, 0xAA }, packet.AsSpan(packet.Length - 2).ToArray());
            Assert.Equal(packet.Length, packet[9]);
            Assert.Equal((uint)expectedSequence, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(10, 4)));

            var group = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(3, 4));
            var packetIndex = packet[7];
            var packetsInGroup = packet[8];
            if (group != currentGroup)
            {
                Assert.Equal(currentGroup + 1, group);
                currentGroup = group;
                expectedPacketIndex = 1;
                expectedPacketsInGroup = packetsInGroup;
            }

            Assert.Equal(expectedPacketIndex, packetIndex);
            Assert.Equal(expectedPacketsInGroup, packetsInGroup);
            Assert.Equal(
                (byte)(packetIndex == 1 ? 0xF1 : packetIndex == packetsInGroup ? 0xF3 : 0xF2),
                packet[2]);
            Assert.All(
                packet.AsSpan(14, packet.Length - 16).ToArray(),
                value => Assert.Equal((byte)(0x40 + packetIndex), value));
            expectedPacketIndex = packetIndex == packetsInGroup ? 0 : packetIndex + 1;
            Assert.False(chunk.EndsAtNativeIdleBoundary);
        }

        Assert.True(observedLengths.Count > 1);
        Assert.True(service.MockGeneratedLineCount >= 12);
        Assert.True(service.MockLastGeneratedSequence >= 12);
        service.StopMockStress();
    }
}
