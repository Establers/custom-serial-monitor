namespace SerialMonitor.WinUI.Models;

public sealed record PluginGuideSnippet(string Title, string Text);
public sealed record PluginGuideRow(string Name, string Detail);
public sealed record PluginGuideSection(string Title, string Body,
    IReadOnlyList<PluginGuideSnippet> Snippets, IReadOnlyList<PluginGuideRow> Rows)
{
    public static PluginGuideSection Text(string title, string body) => new(title, body, [], []);
}
