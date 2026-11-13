using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.WinUI.Models;

// Configuration only: never persist connection ownership, live values or running state.
public sealed class ModbusMonitorSettings
{
    public int UnitId { get; set; } = 1;
    public byte Function { get; set; } = 3;
    public int Address { get; set; }
    public int Quantity { get; set; } = 10;
    public int IntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 1000;
    [System.Text.Json.Serialization.JsonConverter(typeof(ModbusValueTypeJsonConverter))]
    public ModbusValueType ValueType { get; set; } = ModbusValueType.UInt16;
    public double Scale { get; set; } = 1;
    public bool SwapBytes { get; set; }
    public bool SwapWords { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ModbusReadRequest Request => new(UnitId, Function, Address, Quantity);

    public void Validate()
    {
        Request.Validate();
        if (IntervalMs is < 100 or > 60000) throw new ArgumentOutOfRangeException(nameof(IntervalMs));
        if (TimeoutMs is < 100 or > 10000) throw new ArgumentOutOfRangeException(nameof(TimeoutMs));
        if (!Enum.IsDefined(ValueType)) throw new ArgumentOutOfRangeException(nameof(ValueType));
        if (!double.IsFinite(Scale)) throw new ArgumentOutOfRangeException(nameof(Scale));
    }

    public ModbusMonitorSettings Clone() => (ModbusMonitorSettings)MemberwiseClone();
}
