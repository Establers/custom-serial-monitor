using System.Buffers.Binary;
using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusPacketBuilderViewModel
{
    public Visibility ReadRangeVisibility => Show(Function is >= 1 and <= 4 or 23);
    public Visibility SingleRegisterVisibility => Show(Function == 6);
    public Visibility WriteRangeVisibility => Show(Function is 15 or 16);
    public Visibility FifoVisibility => Show(Function == 24);
    public Visibility DiagnosticSingleDataVisibility => Show(DiagnosticSubFunction != 0);
    public Visibility DiagnosticMultipleDataVisibility => Show(DiagnosticSubFunction == 0);
    public int DiagnosticSelectorColumnSpan => DiagnosticSubFunction == 0 ? 2 : 1;
    public string ScalarValueLabel => UiText.Get("ModbusBuilderScalarValue", "Value (0–65535)");
    public string WriteQuantityLabel => UiText.Get("ModbusBuilderWriteQuantity", "Write quantity (auto)");
    public string CompactFunctionLabel => UiText.Get("ModbusBuilderCompactFunction", "Function (1–127)");
    public string CompactNumberHelp => UiText.Get("ModbusBuilderCompactNumbers", "Decimal or 0x HEX · addresses start at 0");
    public string WriteQuantityText
    {
        get
        {
            // FunctionIndex notifications can be observed before Refresh has
            // replaced the previous function's packet (including short status frames).
            if (!CanUsePacket || _packetFunction != Function || Function is not (15 or 16 or 23)) return "—";
            var offset = Function == 23 ? 8 : 4;
            return _packet.Length >= offset + 2
                ? BinaryPrimitives.ReadUInt16BigEndian(_packet.AsSpan(offset)).ToString(CultureInfo.InvariantCulture) : "—";
        }
    }
}
