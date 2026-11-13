using System.Text.Json.Serialization;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.WinUI.Models;

public enum ModbusCommandVerificationMode { ReadBack, Status, AcknowledgementOnly }

// A command owns its explicit write targets. It never inherits the selected
// polling group, and restoring configuration never executes a command.
[JsonConverter(typeof(ModbusCommandSettingsJsonConverter))]
public sealed class ModbusCommandSettings
{
    public const int MaximumCommands = 32;

    public List<ModbusCommandDefinition> Commands { get; set; } = [];
    public string? SelectedCommandId { get; set; }
    [JsonIgnore]
    internal bool HasInvalidMetadata { get; set; }

    public ModbusCommandSettings Clone() => new()
    {
        Commands = Commands?.Select(command => command?.Clone()!).ToList()!,
        SelectedCommandId = SelectedCommandId, HasInvalidMetadata = HasInvalidMetadata
    };

    public void Validate()
    {
        if (HasInvalidMetadata) throw new ArgumentException("Command settings contain invalid imported metadata. Correct them before transmitting.");
        if (Commands is null || Commands.Count > MaximumCommands)
            throw new ArgumentException($"At most {MaximumCommands} Modbus commands are allowed.", nameof(Commands));
        if (SelectedCommandId is not null) ModbusCommandDefinition.ValidateId(SelectedCommandId, nameof(SelectedCommandId));
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in Commands)
        {
            if (command is null) throw new ArgumentException("A saved Modbus command is missing.", nameof(Commands));
            command.Validate();
            if (!identifiers.Add(command.Id)) throw new ArgumentException("Modbus command identifiers must be unique.", nameof(Commands));
        }
    }
}

public sealed class ModbusCommandDefinition
{
    public const int MaximumSteps = 32;
    public const int MaximumDurationMilliseconds = 300000;
    public const int MaximumNameLength = 128;
    public const int MaximumIdLength = 64;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<ModbusCommandStep> Steps { get; set; } = [];
    [JsonIgnore]
    internal bool HasInvalidMetadata { get; set; }

    public ModbusCommandDefinition Clone() => new()
    {
        Id = Id, Name = Name, Steps = Steps?.Select(step => step?.Clone()!).ToList()!, HasInvalidMetadata = HasInvalidMetadata
    };

    public void Validate()
    {
        if (HasInvalidMetadata) throw new ArgumentException("A command contains invalid imported metadata. Correct it before transmitting.");
        ValidateId(Id, nameof(Id));
        ValidateName(Name, nameof(Name));
        if (Steps is null || Steps.Count is < 1 or > MaximumSteps)
            throw new ArgumentException($"A Modbus command must contain 1–{MaximumSteps} steps.", nameof(Steps));
        long duration = 0;
        foreach (var step in Steps)
        {
            if (step is null) throw new ArgumentException("A saved Modbus command step is missing.", nameof(Steps));
            // Preflight every write and result condition before the first TX.
            step.Validate();
            duration += step.TimeoutMs + (step.Verification.Mode switch
            {
                ModbusCommandVerificationMode.ReadBack => step.TimeoutMs,
                ModbusCommandVerificationMode.Status => step.Verification.TimeoutMs,
                _ => 0
            });
            if (duration > MaximumDurationMilliseconds)
                throw new ArgumentException($"A command's configured timeout budget must not exceed {MaximumDurationMilliseconds} ms.", nameof(Steps));
        }
    }

    internal static void ValidateId(string? id, string parameter)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaximumIdLength || id.Any(char.IsControl) || id != id.Trim())
            throw new ArgumentException($"Command identifiers must contain 1–{MaximumIdLength} printable characters without surrounding whitespace.", parameter);
    }

    internal static void ValidateName(string? name, string parameter)
    {
        if (name is null || name.Length > MaximumNameLength || name.Any(char.IsControl))
            throw new ArgumentException($"Names must contain at most {MaximumNameLength} printable characters.", parameter);
    }
}

public sealed class ModbusCommandStep
{
    public const int MaximumValueLength = 256;
    public const int MinimumTimeoutMs = 100;
    public const int MaximumTimeoutMs = 10000;

    public string Name { get; set; } = "";
    public int UnitId { get; set; } = 1;
    // The write area's read function: 01 for coils, 03 for holding registers.
    public byte Function { get; set; } = 3;
    public ModbusItemDefinition Item { get; set; } = new() { Access = ModbusItemAccess.ReadWrite };
    public string Value { get; set; } = "";
    public int TimeoutMs { get; set; } = 1000;
    public ModbusCommandVerification Verification { get; set; } = new();
    [JsonIgnore]
    internal bool HasInvalidMetadata { get; set; }

    public ModbusCommandStep Clone() => new()
    {
        Name = Name, UnitId = UnitId, Function = Function, Item = Item?.Clone()!, Value = Value,
        TimeoutMs = TimeoutMs, Verification = Verification?.Clone()!, HasInvalidMetadata = HasInvalidMetadata
    };

    public void Validate()
    {
        if (HasInvalidMetadata) throw new ArgumentException("A command step contains invalid imported metadata. Correct it before transmitting.");
        ModbusCommandDefinition.ValidateName(Name, nameof(Name));
        if (UnitId is < 1 or > 247) throw new ArgumentOutOfRangeException(nameof(UnitId));
        if (Function is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(Function), "Command writes require coils or holding registers.");
        if (TimeoutMs is < MinimumTimeoutMs or > MaximumTimeoutMs) throw new ArgumentOutOfRangeException(nameof(TimeoutMs));
        if (Item is null) throw new ArgumentException("The command step's item is missing.", nameof(Item));
        Item.Validate(Function == 1);
        if (Item.Access != ModbusItemAccess.ReadWrite) throw new ArgumentException("A command step must explicitly define a writable item.", nameof(Item));
        if (string.IsNullOrWhiteSpace(Value) || Value.Length > MaximumValueLength)
            throw new ArgumentException($"Enter one item value of at most {MaximumValueLength} characters.", nameof(Value));
        if (Verification is null) throw new ArgumentException("The command step's verification settings are missing.", nameof(Verification));
        Verification.Validate();
        _ = ModbusItemCodec.Encode(Value, Item, Function == 1);
        if (Verification.Mode == ModbusCommandVerificationMode.ReadBack)
            new ModbusReadRequest(UnitId, Function, Item.Address, Item.RegisterCount).Validate();
    }

    public ushort[] EncodeValues()
    {
        Validate();
        return ModbusItemCodec.Encode(Value, Item, Function == 1);
    }

    public byte[] BuildPacket()
    {
        var values = EncodeValues();
        var function = Function == 1 ? (byte)5 : values.Length == 1 ? (byte)6 : (byte)16;
        return values.Length == 1
            ? ModbusRtuRequestBuilder.WriteSingle(UnitId, function, Item.Address, values[0])
            : ModbusRtuRequestBuilder.WriteMultiple(UnitId, function, Item.Address, values);
    }
}

public sealed class ModbusCommandVerification
{
    public const int MinimumTimeoutMs = 100;
    public const int MaximumTimeoutMs = 60000;
    public const int MinimumIntervalMs = 100;
    public const int MaximumIntervalMs = 60000;

    public ModbusCommandVerificationMode Mode { get; set; } = ModbusCommandVerificationMode.ReadBack;
    public byte Function { get; set; } = 3;
    public int Address { get; set; }
    public ushort Mask { get; set; } = ushort.MaxValue;
    public ushort ExpectedValue { get; set; }
    public int TimeoutMs { get; set; } = 3000;
    public int IntervalMs { get; set; } = 100;
    [JsonIgnore]
    internal bool HasInvalidMetadata { get; set; }

    public ModbusCommandVerification Clone() => (ModbusCommandVerification)MemberwiseClone();

    public void Validate(bool? isCoil = null)
    {
        if (HasInvalidMetadata) throw new ArgumentException("A result condition contains invalid imported metadata. Correct it before transmitting.");
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (Function is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(Function));
        if (Address is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Address));
        if (Mask == 0 || (ExpectedValue & ~Mask) != 0)
            throw new ArgumentException("The result mask must be nonzero and include every expected bit.", nameof(Mask));
        if ((isCoil ?? Function <= 2) && (Mask != 1 || ExpectedValue > 1))
            throw new ArgumentException("A coil/input result uses mask 1 and expected value 0 or 1.", nameof(Mask));
        if (TimeoutMs is < MinimumTimeoutMs or > MaximumTimeoutMs) throw new ArgumentOutOfRangeException(nameof(TimeoutMs));
        if (IntervalMs is < MinimumIntervalMs or > MaximumIntervalMs || IntervalMs > TimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(IntervalMs));
    }
}
