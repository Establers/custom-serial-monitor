using System.Text.Json.Serialization;

namespace SerialMonitor.WinUI.Models;

// Only configuration is persisted. IDs identify group snapshots and write
// targets independently of the selected view or a device's numeric address.
public sealed class ModbusPollingGroup
{
    public const int MaximumNameLength = 128;
    public const int MaximumIdLength = 64;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ModbusMonitorSettings Settings { get; set; } = new();

    [JsonIgnore]
    internal bool HasInvalidMetadata { get; set; }

    public ModbusPollingGroup Clone() => new()
    {
        Id = Id, Name = Name, Enabled = Enabled, Settings = Settings?.Clone()!,
        HasInvalidMetadata = HasInvalidMetadata
    };

    public void Validate()
    {
        if (HasInvalidMetadata)
            throw new ArgumentException("A polling group contains invalid imported metadata. Correct it before transmitting.");
        ValidateId(Id, nameof(Id));
        if (Name is null || Name.Length > MaximumNameLength || Name.Any(char.IsControl))
            throw new ArgumentException($"Group names must contain at most {MaximumNameLength} printable characters.", nameof(Name));
        if (Settings is null) throw new ArgumentException("The polling group's settings are missing.", nameof(Settings));
        Settings.Validate();
    }

    internal static void ValidateId(string? id, string parameter)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaximumIdLength || id.Any(char.IsControl) || id != id.Trim())
            throw new ArgumentException($"Group identifiers must contain 1–{MaximumIdLength} printable characters without surrounding whitespace.", parameter);
    }
}

[JsonConverter(typeof(ModbusPollingSettingsJsonConverter))]
public sealed class ModbusPollingSettings
{
    public const int MaximumGroups = 32;
    public const int MaximumTotalCells = 8192;

    public List<ModbusPollingGroup> Groups { get; set; } = [];
    public string? SelectedGroupId { get; set; }

    [JsonIgnore]
    internal bool HasInvalidMetadata { get; set; }

    public ModbusPollingSettings Clone() => new()
    {
        Groups = Groups?.Select(group => group?.Clone()!).ToList()!,
        SelectedGroupId = SelectedGroupId, HasInvalidMetadata = HasInvalidMetadata
    };

    public void Validate(bool requireEnabled = false)
    {
        if (HasInvalidMetadata)
            throw new ArgumentException("Polling settings contain invalid imported metadata. Correct them before transmitting.");
        if (Groups is null || Groups.Count > MaximumGroups)
            throw new ArgumentException($"At most {MaximumGroups} polling groups are allowed.", nameof(Groups));
        if (SelectedGroupId is not null) ModbusPollingGroup.ValidateId(SelectedGroupId, nameof(SelectedGroupId));

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        long totalCells = 0;
        var anyEnabled = false;
        foreach (var group in Groups)
        {
            if (group is null) throw new ArgumentException("A polling group is missing.", nameof(Groups));
            group.Validate();
            if (!identifiers.Add(group.Id)) throw new ArgumentException("Polling group identifiers must be unique.", nameof(Groups));
            totalCells += group.Settings.Quantity;
            if (totalCells > MaximumTotalCells)
                throw new ArgumentException($"Polling groups may contain at most {MaximumTotalCells} raw cells in total.", nameof(Groups));
            anyEnabled |= group.Enabled;
        }
        if (requireEnabled && !anyEnabled)
            throw new ArgumentException("Enable at least one polling group before starting.", nameof(Groups));
    }
}
