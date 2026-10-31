using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Services;

var directory = Path.GetFullPath(args.Single());
var files = Directory.GetFiles(directory, "*.gui.json");
var sequences = await new CommandSequenceFileService().LoadAsync(files, CancellationToken.None);
if (sequences.Count != 5) throw new Exception("Expected five GUI examples.");
foreach (var sequence in sequences)
    Console.WriteLine($"PASS app GUI importer: {sequence.Name} ({sequence.Steps.Count} steps)");
foreach (var file in Directory.GetFiles(directory, "*.cli.json"))
{
    using var json = JsonDocument.Parse(File.ReadAllText(file));
    var cli = AutomationSequenceValidation.Parse(json.RootElement);
    Console.WriteLine($"PASS app CLI validator: {cli.Name} ({cli.Steps.Count} steps)");
}
