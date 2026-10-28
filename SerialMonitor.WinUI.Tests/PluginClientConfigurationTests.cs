using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class PluginClientConfigurationTests
{
    private const string ToolPath = "C:\\Serial Monitor\\한글 $tools\u0060 O'Brien\\serialctl.exe";

    [Theory]
    [InlineData(0, "claude plugin marketplace add", "claude plugin install")]
    [InlineData(1, "codex plugin marketplace add", "codex plugin add")]
    [InlineData(2, "copilot plugin marketplace add", "copilot plugin install")]
    [InlineData(3, "/plugin marketplace add", "/plugin install")]
    [InlineData(5, "/plugin marketplace add", "/plugin install")]
    public void GitInstallation_UsesClientCommandsAndStableIdentity(int clientValue, string marketplace, string install)
    {
        var client = (PluginClient)clientValue;
        var command = PluginClientConfiguration.InstallCommands(client);
        Assert.StartsWith(marketplace + " ", command);
        Assert.Contains(PluginClientConfiguration.GitRepositoryUrl, command);
        Assert.Contains(install + " " + PluginClientConfiguration.PluginId, command);
        Assert.DoesNotContain("mcp add", command);
        Assert.DoesNotContain("SetEnvironmentVariable", command);
    }

    [Fact]
    public void EnvironmentAndDiscoveryCommands_PreserveExecutablePathAsLiteral()
    {
        Assert.Equal("$env:SERIAL_MONITOR_CLI = 'C:\\Serial Monitor\\한글 $tools\u0060 O''Brien\\serialctl.exe'",
            PluginClientConfiguration.TemporaryCliEnvironment(ToolPath));
        Assert.Equal("& 'C:\\Serial Monitor\\한글 $tools\u0060 O''Brien\\serialctl.exe' describe --json",
            PluginClientConfiguration.DiscoveryCommand(ToolPath));
    }

    [Fact]
    public void CodexIdeExtension_ProvidesMcpFallbackInsteadOfUnsupportedPluginCommands()
    {
        Assert.False(PluginClientConfiguration.SupportsPlugin(PluginClient.CodexVsCode));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginClientConfiguration.InstallCommands(PluginClient.CodexVsCode));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginClientConfiguration.UpdateCommands(PluginClient.CodexVsCode));
    }

    [Fact]
    public void UpdateCommands_DistinguishMarketplaceRefreshFromPluginUpdate()
    {
        Assert.Equal("codex plugin marketplace upgrade serial-monitor-tools\ncodex plugin list",
            PluginClientConfiguration.UpdateCommands(PluginClient.Codex));
        Assert.Equal("claude plugin update serial-monitor@serial-monitor-tools",
            PluginClientConfiguration.UpdateCommands(PluginClient.ClaudeCode));
        Assert.Equal("copilot plugin marketplace update serial-monitor-tools\ncopilot plugin update serial-monitor@serial-monitor-tools",
            PluginClientConfiguration.UpdateCommands(PluginClient.CopilotCli));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void QuickStartAlwaysUsesGitHubWithoutRequiringAnExecutablePath(int clientIndex)
    {
        var model = new PluginGuideViewModel { SelectedClientIndex = clientIndex, ToolPath = "missing.exe" };
        Assert.Equal(2, model.Sections.Count);
        Assert.Contains(model.Sections[0].Snippets, snippet => snippet.Text.Contains(PluginClientConfiguration.GitRepositoryUrl));
        Assert.Contains(model.Sections.SelectMany(section => section.Snippets), snippet => snippet.Text.Contains("serial_get_capabilities"));
        Assert.Equal(Visibility.Collapsed, model.ToolPathVisibility);
        Assert.DoesNotContain(model.Sections.SelectMany(section => section.Snippets), snippet => snippet.Text.Contains("SetEnvironmentVariable"));
    }

    [Fact]
    public void ExecutablePathGuidanceAppearsOnlyInTroubleshootingOrIdeFallback()
    {
        var model = new PluginGuideViewModel { SelectedClientIndex = (int)PluginClient.Codex };
        model.SelectedPageIndex = 1;
        Assert.Equal(Visibility.Collapsed, model.ToolPathVisibility);
        model.SelectedPageIndex = 3;
        Assert.Equal(Visibility.Visible, model.ToolPathVisibility);
        model.SelectedPageIndex = 0;
        model.SelectedClientIndex = (int)PluginClient.CodexVsCode;
        Assert.Equal(Visibility.Visible, model.ToolPathVisibility);
    }
}