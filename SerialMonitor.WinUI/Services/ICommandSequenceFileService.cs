using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface ICommandSequenceFileService
{
    // Returns detached, validated, normalized and numbered sequences ready for UI publication.
    Task<IReadOnlyList<CommandSequence>> LoadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
    Task ExportAsync(string path, CommandSequence sequence, CancellationToken cancellationToken);
}
