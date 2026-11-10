using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusAdvancedPacketBuilderViewModelTests
{
    [Fact]
    public void EveryStandardFunctionAndCustomHaveAValidDefaultAndOnlyRelevantFields()
    {
        byte[] functions = [1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 15, 16, 17, 20, 21, 22, 23, 24, 43, 0];
        Assert.Equal(functions.Length, new ModbusPacketBuilderViewModel().FunctionOptions.Count);
        foreach (var function in functions)
        {
            var model = new ModbusPacketBuilderViewModel();
            model.SelectFunction(function);
            Assert.True(model.CanUsePacket, $"{function:X2}: {model.ValidationError}");
            Assert.Equal(0, ModbusRtuCrc.Compute(model.Packet.Span));
            Assert.Equal(function == 0 ? 65 : function, model.Packet.Span[1]);
            if (function is 7 or 8 or 11 or 12 or 17 or 20 or 21 or 43 or 0)
                Assert.Equal(Visibility.Collapsed, model.AddressVisibility);
        }
    }

    [Fact]
    public void ReadWriteSeparatesReadAndWriteAddressesQuantitiesAndBounds()
    {
        var model = new ModbusPacketBuilderViewModel { AddressText = "3", QuantityText = "6", WriteAddressText = "14", ValuesText = "255 255 255" };
        model.SelectFunction(23);
        Assert.True(model.CanUsePacket);
        Assert.Equal(Convert.FromHexString("011700030006000E00030600FF00FF00FF"), model.Packet.Span[..^2].ToArray());
        Assert.Equal(Visibility.Visible, model.ReadWriteVisibility);
        model.QuantityText = "125";
        model.ValuesText = string.Join(" ", Enumerable.Repeat("0", 121));
        Assert.True(model.CanUsePacket);
        model.ValuesText += " 0";
        Assert.False(model.CanUsePacket);
        Assert.True(model.Packet.IsEmpty);
        model.ValuesText = "1 2";
        model.WriteAddressText = "65535";
        Assert.False(model.CanUsePacket);
    }

    [Fact]
    public void FileEditorsBuildMultipleGroupsAndRejectMalformedOrOversizedRequests()
    {
        var model = new ModbusPacketBuilderViewModel { FileReadText = "4, 1, 2\n3, 9, 2" };
        model.SelectFunction(20);
        Assert.True(model.CanUsePacket);
        Assert.Equal(Convert.FromHexString("01140E0600040001000206000300090002"), model.Packet.Span[..^2].ToArray());
        foreach (var invalid in new[] { "", "1, 0", "1, 0, 62\n2, 0, 62", "1, 9999, 2", new string('1', 12001) })
        {
            model.FileReadText = invalid;
            Assert.False(model.CanUsePacket);
            Assert.True(model.Packet.IsEmpty);
        }
        model.SelectFunction(21);
        model.FileWriteText = "4, 7: 0x06AF 0x04BE 0x100D\n3, 9: 0xABCD";
        Assert.True(model.CanUsePacket);
        model.FileWriteText = "1, 0, 1";
        Assert.False(model.CanUsePacket);
    }

    [Fact]
    public void DiagnosticsRevalidateTheSelectedSubfunctionAndRetainTheDraft()
    {
        var model = new ModbusPacketBuilderViewModel { DiagnosticDataText = "0xA537, 0x1234" };
        model.SelectFunction(8);
        Assert.True(model.CanUsePacket);
        Assert.Equal(Convert.FromHexString("01080000A5371234"), model.Packet.Span[..^2].ToArray());
        model.DiagnosticIndex = 1;
        Assert.False(model.CanUsePacket);
        Assert.Equal("0xA537, 0x1234", model.DiagnosticDataText);
        model.DiagnosticDataText = "0xFF00";
        Assert.True(model.CanUsePacket);
        model.DiagnosticIndex = 2;
        Assert.False(model.CanUsePacket);
        model.DiagnosticDataText = "0";
        Assert.True(model.CanUsePacket);
        Assert.Equal(15, model.DiagnosticOptions.Count);
    }

    [Fact]
    public void MaskAndDeviceFieldsDoNotLeakAcrossFunctionsOrAllowByteWrapping()
    {
        var model = new ModbusPacketBuilderViewModel { AddressText = "4", AndMaskText = "0xF2", OrMaskText = "0x25" };
        model.SelectFunction(22);
        Assert.Equal(Convert.FromHexString("0116000400F20025"), model.Packet.Span[..^2].ToArray());
        model.SelectFunction(43);
        model.AddressText = "invalid hidden address";
        model.DeviceAccessIndex = 3;
        model.ObjectIdText = "0x80";
        Assert.True(model.CanUsePacket);
        Assert.Equal(Convert.FromHexString("012B0E0480"), model.Packet.Span[..^2].ToArray());
        model.ObjectIdText = "0x100";
        Assert.False(model.CanUsePacket);
        model.SelectFunction(7);
        Assert.True(model.CanUsePacket);
        Assert.Equal(4, model.Packet.Length);
    }

    [Fact]
    public void CustomDataUsesByteHexRatherThanDecimalWordsAndHasAnAduLimit()
    {
        var model = new ModbusPacketBuilderViewModel { CustomFunctionText = "0x41", CustomDataText = "00 FF80" };
        model.SelectFunction(0);
        Assert.Equal(Convert.FromHexString("014100FF80"), model.Packet.Span[..^2].ToArray());
        foreach (var invalid in new[] { "ABC", "GG", new string('0', 506), new string('0', 4097) })
        {
            model.CustomDataText = invalid;
            Assert.False(model.CanUsePacket);
            Assert.True(model.Packet.IsEmpty);
        }
        model.CustomDataText = new string('0', 504);
        Assert.True(model.CanUsePacket);
        Assert.Equal(256, model.Packet.Length);
        model.UnitText = "0";
        Assert.False(model.CanUsePacket);
        model.SelectFunction(22);
        Assert.True(model.CanUsePacket);
    }
}
