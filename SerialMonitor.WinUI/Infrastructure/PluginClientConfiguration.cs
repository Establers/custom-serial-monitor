namespace SerialMonitor.WinUI.Infrastructure;

internal enum PluginClient
{
    ClaudeCode, Codex, CopilotCli, ClaudeVsCode, CodexVsCode, CopilotVsCode
}

// Generates reviewable instructions only. Never launches an installer or edits host settings.
internal static class PluginClientConfiguration
{
    public const string Repository = "Establers/custom-serial-monitor";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    public const string GitRepositoryUrl = RepositoryUrl + ".git";
    public const string Marketplace = "serial-monitor-tools";
    public const string PluginName = "serial-monitor";
    public const string PluginId = PluginName + "@" + Marketplace;

    public static bool SupportsPlugin(PluginClient client) => client != PluginClient.CodexVsCode;
    public static bool UsesChatCommands(PluginClient client) => client is PluginClient.ClaudeVsCode or PluginClient.CopilotVsCode;

    public static string InstallCommands(PluginClient client)
    {
        RequirePluginClient(client);
        var quoted = QuotePowerShellLiteral(GitRepositoryUrl);
        return client switch
        {
            PluginClient.Codex => $"codex plugin marketplace add {quoted}\ncodex plugin add {PluginId}",
            PluginClient.ClaudeCode => $"claude plugin marketplace add {quoted}\nclaude plugin install {PluginId} --scope user",
            PluginClient.CopilotCli => $"copilot plugin marketplace add {quoted}\ncopilot plugin install {PluginId}",
            PluginClient.ClaudeVsCode => $"/plugin marketplace add {GitRepositoryUrl}\n/plugin install {PluginId}",
            PluginClient.CopilotVsCode => $"/plugin marketplace add {GitRepositoryUrl}\n/plugin install {PluginId}",
            _ => throw new ArgumentOutOfRangeException(nameof(client))
        };
    }

    public static string UpdateCommands(PluginClient client)
    {
        RequirePluginClient(client);
        return client switch
        {
            PluginClient.Codex => $"codex plugin marketplace upgrade {Marketplace}\ncodex plugin list",
            PluginClient.ClaudeCode => $"claude plugin update {PluginId}",
            PluginClient.CopilotCli => $"copilot plugin marketplace update {Marketplace}\ncopilot plugin update {PluginId}",
            PluginClient.ClaudeVsCode => $"claude plugin update {PluginId}",
            PluginClient.CopilotVsCode => $"/plugin marketplace update {Marketplace}\n/plugin update {PluginId}",
            _ => throw new ArgumentOutOfRangeException(nameof(client))
        };
    }

    public static string TemporaryCliEnvironment(string path)
    {
        RequireToolPath(path);
        return $"$env:SERIAL_MONITOR_CLI = {QuotePowerShellLiteral(path)}";
    }

    public static string DiscoveryCommand(string path)
    {
        RequireToolPath(path);
        return $"& {QuotePowerShellLiteral(path)} describe --json";
    }

    internal static string QuotePowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private static void RequireToolPath(string path)
    {
        if (!McpClientConfiguration.TryNormalizePath(path, out var normalized) || path != normalized)
            throw new ArgumentException("Use an absolute Windows .exe path without surrounding quotes.", nameof(path));
    }

    private static void RequirePluginClient(PluginClient client)
    {
        if (!Enum.IsDefined(client) || !SupportsPlugin(client))
            throw new ArgumentOutOfRangeException(nameof(client), "Use MCP configuration for the Codex IDE extension.");
    }
}
