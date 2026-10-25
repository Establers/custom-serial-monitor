namespace SerialMonitor.WinUI.Models;

public sealed record McpGuideSnippet(string Title, string Text);
public sealed record McpGuideRow(string Name, string Detail);
public sealed record McpGuideSection(string Title, string Body,
    IReadOnlyList<McpGuideSnippet> Snippets, IReadOnlyList<McpGuideRow> Rows)
{
    public static McpGuideSection Text(string title, string body) => new(title, body, [], []);
}
