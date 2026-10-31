namespace SerialMonitor.WinUI.Models;

public sealed class AppProfile
{
    public int ProfileSchemaVersion { get; set; } = 1;

    public string Name { get; set; } = "Default";

    public string CurrentSessionName { get; set; } = string.Empty;

    public SerialSettings SerialSettings { get; set; } = new();

    public SerialSettings? LastSuccessfulSerialSettings { get; set; }

    public LogSettings LogSettings { get; set; } = new();

    public UiSettings UiSettings { get; set; } = new();

    public EventContextSettings EventContextSettings { get; set; } = new();

    public BridgeSettings BridgeSettings { get; set; } = new();

    public List<LogRule> LogRules { get; set; } = new();

    public List<EventRule> EventRules { get; set; } = new();

    public List<HighlightRule> HighlightRules { get; set; } = new();

    public List<TxCommand> SavedCommands { get; set; } = new();

    public List<CommandHistoryEntry> CommandHistory { get; set; } = new();

    public Dictionary<int, InstanceInputHistory> InstanceHistories { get; set; } = new();

    public Dictionary<string, InstanceInputHistory> PortHistories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int PortHistoryMigrationVersion { get; set; }

    public InstanceInputHistory? LegacyInputHistory { get; set; }

    // A connected port can differ from a selection pending the next reconnect.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ActiveHistoryPort { get; set; }

    public List<CommandSequence> CommandSequences { get; set; } = new();
}
