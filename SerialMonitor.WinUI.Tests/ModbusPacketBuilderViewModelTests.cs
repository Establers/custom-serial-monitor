using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusPacketBuilderViewModelTests
{
    [Fact]
    public void DefaultAndHexFieldsProduceTheKnownRequest()
    {
        var model = new ModbusPacketBuilderViewModel { UnitText = "0x01", AddressText = "0X0000", QuantityText = "2" };
        Assert.True(model.CanUsePacket);
        Assert.Equal("01 03 00 00 00 02 C4 0B", model.Preview);
        Assert.Equal(Visibility.Visible, model.QuantityVisibility);
        Assert.Equal(Visibility.Collapsed, model.ValuesVisibility);
    }

    [Theory]
    [InlineData(0, "0", "1")]
    [InlineData(3, "1", "126")]
    [InlineData(2, "248", "1")]
    [InlineData(2, "1", "-1")]
    [InlineData(2, "1", "0xFFFFFFFF")]
    [InlineData(2, "1", "65536")]
    public void InvalidInputClearsPreviouslyValidPacket(int functionIndex, string unit, string quantity)
    {
        var model = new ModbusPacketBuilderViewModel();
        Assert.True(model.CanUsePacket);
        model.FunctionIndex = functionIndex;
        model.UnitText = unit;
        model.QuantityText = quantity;
        Assert.False(model.CanUsePacket);
        Assert.True(model.Packet.IsEmpty);
        Assert.Empty(model.Preview);
        Assert.NotEmpty(model.ValidationError);
    }

    [Fact]
    public void FunctionChangesRevalidateBroadcastAndHiddenInputs()
    {
        var model = new ModbusPacketBuilderViewModel { UnitText = "0" };
        Assert.False(model.CanUsePacket);
        model.FunctionIndex = 4;
        model.QuantityText = "invalid hidden quantity";
        model.ValuesText = "invalid hidden values";
        model.CoilOn = true;
        Assert.True(model.CanUsePacket);
        Assert.Equal(Visibility.Visible, model.CoilVisibility);
        Assert.Equal(0xFF, model.Packet.Span[4]);
        model.FunctionIndex = 2;
        Assert.False(model.CanUsePacket);
        model.UnitText = "1";
        model.QuantityText = "1";
        Assert.True(model.CanUsePacket);
    }

    [Fact]
    public void ValuesDeriveCountsAndPreserveAddressOrder()
    {
        var model = new ModbusPacketBuilderViewModel { FunctionIndex = 7, AddressText = "0x10", ValuesText = "10, 0xABCD; 65535\n0" };
        Assert.True(model.CanUsePacket);
        Assert.Equal(Convert.FromHexString("01100010000408000AABCDFFFF0000"), model.Packet.Span[..^2].ToArray());
        Assert.Equal(0, ModbusRtuCrc.Compute(model.Packet.Span));
        model.FunctionIndex = 5;
        Assert.False(model.CanUsePacket); // FC 06 requires exactly one register.
        model.ValuesText = "0xFFFF";
        Assert.True(model.CanUsePacket);
        model.AddressText = "65535";
        model.FunctionIndex = 7;
        model.ValuesText = "1 2";
        Assert.False(model.CanUsePacket);
    }

    [Fact]
    public void CoilListsRejectNonBitsAndExcessiveInputAndRecover()
    {
        var model = new ModbusPacketBuilderViewModel { FunctionIndex = 6, ValuesText = "1,0,1,0,1,0,1,0,1" };
        Assert.True(model.CanUsePacket);
        Assert.Equal(new byte[] { 0x55, 0x01 }, model.Packet.Span[7..^2].ToArray());
        foreach (var invalid in new[] { "", "1 2", "ON OFF", new string('1', 12001), string.Join(" ", Enumerable.Repeat("1", 1969)) })
        {
            model.ValuesText = invalid;
            Assert.False(model.CanUsePacket);
            Assert.True(model.Packet.IsEmpty);
        }
        model.ValuesText = string.Join(" ", Enumerable.Repeat("1", 1968));
        Assert.True(model.CanUsePacket);
        Assert.Equal(255, model.Packet.Length);
    }

    [Fact]
    public async Task PacketBuilderIsOnlyVisibleForModbusAndDoesNotChangeAnalyzerState()
    {
        await using var analyzer = new ProtocolAnalyzer();
        var model = new ProtocolViewModel(analyzer);
        Assert.Equal(Visibility.Collapsed, model.PacketBuilderVisibility);
        model.SelectedProtocolIndex = 1;
        Assert.Equal(Visibility.Visible, model.PacketBuilderVisibility);
        _ = new ModbusPacketBuilderViewModel();
        Assert.Empty(analyzer.Snapshot().Results!);
        model.SelectedProtocolIndex = 2;
        Assert.Equal(Visibility.Collapsed, model.PacketBuilderVisibility);
        model.SelectedProtocolIndex = 0;
        Assert.Equal(Visibility.Collapsed, model.PacketBuilderVisibility);
    }
}
