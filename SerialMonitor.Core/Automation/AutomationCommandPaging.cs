using System.Text.Json;

namespace SerialMonitor.Core.Automation;

public sealed record AutomationSavedCommand(string Name, string Command, string TxMode, string LineEnding, int RecommendedDurationMs = 2000);
public sealed record AutomationCommandPage(IReadOnlyList<AutomationSavedCommand> Commands, int Offset, int NextOffset, bool HasMore, int TotalCount);

public static class AutomationCommandPaging
{
    public static AutomationCommandPage Create(int totalCount, Func<int, AutomationSavedCommand> getCommand, JsonElement args)
    {
        var offset = args.Integer("offset", 0);
        var limit = args.Integer("limit", 100);
        if (offset < 0 || offset > totalCount || limit is < 1 or > 100)
            throw new AutomationException("invalid_argument", "Use offset 0..totalCount and limit 1..100.");
        var commands = new List<AutomationSavedCommand>();
        var bytes = 0;
        for (var index = offset; index < totalCount && commands.Count < limit; index++)
        {
            var command = getCommand(index);
            if (command.Name.Length > 128 || command.Command.Length > 16384)
                throw new AutomationException("definition_too_large", "Saved command at offset " + index + " exceeds the automation limits (name 128, command 16384 characters). Edit or split it in the GUI.");
            var size = JsonSerializer.SerializeToUtf8Bytes(command, AutomationJson.Options).Length;
            // Reserve space for the RPC envelope and MCP's text copy of it.
            if (bytes + size > 128 * 1024 && commands.Count > 0) break;
            commands.Add(command);
            bytes += size;
        }
        var next = offset + commands.Count;
        return new(commands, offset, next, next < totalCount, totalCount);
    }
}
