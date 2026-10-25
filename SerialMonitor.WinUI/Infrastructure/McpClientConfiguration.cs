using System.Text.Json;
using System.Text.Encodings.Web;

namespace SerialMonitor.WinUI.Infrastructure;

internal enum McpClient
{
    ClaudeCli, CodexCli, CopilotCli, ClaudeVsCode, CodexVsCode, CopilotVsCode
}

// Produces text only. Never executes commands or edits a client's configuration.
internal static class McpClientConfiguration
{
    public const int CodexToolTimeoutSeconds = 90;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool TryNormalizePath(string value, out string path)
    {
        path = value.Trim();
        return path.Length > 0 && Path.IsPathFullyQualified(path) &&
            path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
            !path.Any(char.IsControl) && !path.Contains('"') &&
            string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    public static string FindToolPath(string baseDirectory)
    {
        var installed = Path.Combine(baseDirectory, "tools", "serialctl.exe");
        if (File.Exists(installed)) return installed;
        // A source checkout may run the GUI and CLI from separate build folders.
        var ancestor = new DirectoryInfo(baseDirectory);
        for (var i = 0; i < 8 && ancestor is not null; i++, ancestor = ancestor.Parent)
        {
            if (!File.Exists(Path.Combine(ancestor.FullName, "SerialMonitor.Cli", "SerialMonitor.Cli.csproj"))) continue;
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var development = Path.Combine(ancestor.FullName, "SerialMonitor.Cli", "bin", configuration, "net8.0", "serialctl.exe");
                if (File.Exists(development)) return development;
            }
            break;
        }
        return installed;
    }

    public static string RegistrationCommand(McpClient client, string path)
    {
        RequirePath(path);
        // PowerShell literal strings preserve $, backticks, spaces and apostrophes.
        var literal = "'" + path.Replace("'", "''") + "'";
        return client switch
        {
            McpClient.ClaudeCli or McpClient.ClaudeVsCode =>
                $"claude mcp add --transport stdio --scope user serial-monitor -- {literal} mcp",
            McpClient.CodexCli or McpClient.CodexVsCode =>
                $"codex mcp add serial-monitor -- {literal} mcp",
            McpClient.CopilotCli => $"copilot mcp add serial-monitor -- {literal} mcp",
            _ => throw new ArgumentOutOfRangeException(nameof(client))
        };
    }

    public static string Configuration(McpClient client, string path)
    {
        RequirePath(path);
        if (client is McpClient.CodexCli or McpClient.CodexVsCode)
        {
            return $"[mcp_servers.serial-monitor]\ncommand = {JsonSerializer.Serialize(path, JsonOptions)}\nargs = [\"mcp\"]\n{CodexTimeoutSetting}";
        }

        var entry = new Dictionary<string, object>
        {
            ["type"] = "stdio", ["command"] = path, ["args"] = new[] { "mcp" }
        };
        if (client == McpClient.CopilotCli) entry["tools"] = new[] { "*" };
        var root = client == McpClient.CopilotVsCode ? "servers" : "mcpServers";
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [root] = new Dictionary<string, object> { ["serial-monitor"] = entry }
        }, JsonOptions);
    }

    public static string CodexTimeoutSetting => $"tool_timeout_sec = {CodexToolTimeoutSeconds}";

    private static void RequirePath(string path)
    {
        if (!TryNormalizePath(path, out var normalized) || normalized != path)
            throw new ArgumentException("Use an absolute Windows .exe path without surrounding quotes.", nameof(path));
    }
}
