using System.Diagnostics;
using System.Xml.Linq;

namespace SerialMonitor.WinUI.Tests;

public sealed class ShortcutArchitectureBuildTests
{
    [Theory]
    [InlineData(".cs", "if (args.Key == VirtualKey.F2) Run();")]
    [InlineData(".cs", "var accelerator = new KeyboardAccelerator();")]
    [InlineData(".xaml", "<KeyboardAccelerator Key=\"F2\" />")]
    [InlineData(".xaml", "<Button AccessKey=\"A\" />")]
    [InlineData(".js", "if (event.key === 'F2') run();")]
    [InlineData(".html", "<script>if (ev['altKey']) run();</script>")]
    public async Task BuildRejectsBindingsThatBypassTheCatalog(string extension, string source)
    {
        var result = await RunGuardAsync(extension, source);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("KEYMAP001", result.Output);
    }

    [Fact]
    public async Task BuildAllowsActionMatchingThroughTheCatalog()
    {
        var result = await RunGuardAsync(".cs", "if (AppShortcuts.Matches(AppShortcutAction.GoTo, args.Key)) Run();");
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task BuildAllowsUnrelatedDictionaryKeyComparisons()
    {
        var result = await RunGuardAsync(".cs", "if (item.Key == customerId) Update(item.Value);");
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static async Task<(int ExitCode, string Output)> RunGuardAsync(string extension, string source)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "build", "ShortcutArchitecture.targets"))) root = root.Parent;
        Assert.NotNull(root);
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), nameof(ShortcutArchitectureBuildTests)));
        var directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var probe = Path.Combine(directory, "Probe" + extension);
            await File.WriteAllTextAsync(probe, source);
            var project = Path.Combine(directory, "Guard.proj");
            var xml = new XDocument(new XElement("Project",
                new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", probe))),
                new XElement("Import", new XAttribute("Project", Path.Combine(root!.FullName, "build", "ShortcutArchitecture.targets")))));
            await File.WriteAllTextAsync(project, xml.ToString());
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "msbuild", project, "-t:ValidateShortcutArchitecture", "-nologo" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            return (process.ExitCode, await output + await errors);
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Shortcut guard fixture cleanup escaped its test directory.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
