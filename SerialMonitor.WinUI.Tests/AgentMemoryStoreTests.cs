using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class AgentMemoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentMemory-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReopeningPreservesAgentEditsAndCreatesOnlyMissingTemplates()
    {
        Directory.CreateDirectory(_directory);
        var index = Path.Combine(_directory, "INDEX.md");
        await File.WriteAllTextAsync(index, "# Existing products\nUser-maintained aliases and links\n");
        var product = Path.Combine(_directory, "device.md");
        await File.WriteAllTextAsync(product, "Existing protocol evidence");
        await new AgentMemoryStore(_directory).InitializeAsync();
        Assert.Equal("# Existing products\nUser-maintained aliases and links\n", await File.ReadAllTextAsync(index));
        Assert.Equal("Existing protocol evidence", await File.ReadAllTextAsync(product));
        var template = Path.Combine(_directory, "PRODUCT_TEMPLATE.md");
        Assert.NotEmpty(await File.ReadAllTextAsync(template));
        await File.WriteAllTextAsync(template, "User's preferred format");
        await new AgentMemoryStore(_directory).InitializeAsync();
        Assert.Equal("User's preferred format", await File.ReadAllTextAsync(template));
    }

    [Fact]
    public async Task ConcurrentWindowsPublishCompleteTemplatesWithoutTemporaryFiles()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new AgentMemoryStore(_directory).InitializeAsync()));
        foreach (var (name, resource) in new[] {
            ("INDEX.md", "SerialMonitor.AgentMemory.Index"),
            ("PRODUCT_TEMPLATE.md", "SerialMonitor.AgentMemory.ProductTemplate") })
        {
            using var input = typeof(AgentMemoryStore).Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(input);
            Assert.Equal(await reader.ReadToEndAsync(), await File.ReadAllTextAsync(Path.Combine(_directory, name)));
        }
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task CanceledInitializationLeavesExistingNotesUnchanged()
    {
        Directory.CreateDirectory(_directory);
        var index = Path.Combine(_directory, "INDEX.md");
        await File.WriteAllTextAsync(index, "Existing notes");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AgentMemoryStore(_directory).InitializeAsync(new CancellationToken(true)));
        Assert.Equal("Existing notes", await File.ReadAllTextAsync(index));
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
