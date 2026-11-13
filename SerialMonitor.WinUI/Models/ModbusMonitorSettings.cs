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

    // Empty keeps the legacy whole-area format. Named definitions can cover a
    // sparse subset; remaining addresses retain the raw register view.
    [System.Text.Json.Serialization.JsonConverter(typeof(ModbusItemDefinitionsJsonConverter))]
    public List<ModbusItemDefinition> Items { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public ModbusReadRequest Request => new(UnitId, Function, Address, Quantity);

    public void Validate()
    {
        Request.Validate();
        if (IntervalMs is < 100 or > 60000) throw new ArgumentOutOfRangeException(nameof(IntervalMs));
        if (TimeoutMs is < 100 or > 10000) throw new ArgumentOutOfRangeException(nameof(TimeoutMs));
        if (!Enum.IsDefined(ValueType)) throw new ArgumentOutOfRangeException(nameof(ValueType));
        if (!double.IsFinite(Scale)) throw new ArgumentOutOfRangeException(nameof(Scale));
        if (Items is null || Items.Count > Quantity)
            throw new ArgumentException("Item definitions must fit inside the monitored area.", nameof(Items));

        var previousEnd = (long)Address;
        foreach (var item in Items.OrderBy(item => item?.Address ?? int.MinValue))
        {
            if (item is null) throw new ArgumentException("An item definition is missing.", nameof(Items));
            item.Validate(Function <= 2);
            var end = (long)item.Address + item.RegisterCount;
            if (item.Address < Address || end > (long)Address + Quantity)
                throw new ArgumentException("An item definition is outside the monitored area.", nameof(Items));
            if (item.Address < previousEnd)
                throw new ArgumentException("Item definitions overlap.", nameof(Items));
            if (Function is 2 or 4 && item.Access == ModbusItemAccess.ReadWrite)
                throw new ArgumentException("Input areas cannot contain writable items.", nameof(Items));
            previousEnd = end;
        }
    }

    public void ValidateWrite(int address, int count)
    {
        Validate();
        if (Function is not (1 or 3)) throw new InvalidOperationException("This area is read-only.");
        if (count <= 0 || address < Address || (long)address + count > (long)Address + Quantity)
            throw new ArgumentOutOfRangeException(nameof(address), "The write must stay inside the monitored area.");

        var end = (long)address + count;
        foreach (var item in Items)
        {
            var itemEnd = (long)item.Address + item.RegisterCount;
            if (end <= item.Address || address >= itemEnd) continue;
            if (item.Access != ModbusItemAccess.ReadWrite)
                throw new InvalidOperationException("The write overlaps a read-only item.");
            if (address > item.Address || end < itemEnd)
                throw new ArgumentException("A write must include every register of a multi-register item.", nameof(count));
        }
    }

    public ModbusMonitorSettings Clone()
    {
        var copy = (ModbusMonitorSettings)MemberwiseClone();
        // Preserve invalid imported definitions for correction instead of
        // silently restoring the legacy writable view.
        copy.Items = Items?.Select(item => item?.Clone()!).ToList()!;
        return copy;
    }
}
