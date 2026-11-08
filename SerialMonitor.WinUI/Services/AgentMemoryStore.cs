namespace SerialMonitor.WinUI.Services;

// Only seeds missing files. Product notes belong to the user and the external agent.
internal sealed class AgentMemoryStore(string? directory = null)
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "Notes");

    public string DirectoryPath { get; } = Path.GetFullPath(directory ?? DefaultDirectory);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        await SeedAsync("INDEX.md", "SerialMonitor.AgentMemory.Index", cancellationToken);
        await SeedAsync("PRODUCT_TEMPLATE.md", "SerialMonitor.AgentMemory.ProductTemplate", cancellationToken);
    }

    private async Task SeedAsync(string name, string resource, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(DirectoryPath, name);
        if (File.Exists(destination)) return;
        var temporary = Path.Combine(DirectoryPath, $".{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = typeof(AgentMemoryStore).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing memory template: {resource}"))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { } // Another window or agent created it.
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
