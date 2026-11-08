namespace SerialMonitor.WinUI.Services;

public interface IBackgroundImageStore
{
    Task<string> ImportAsync(string profilePath, int presetNumber, string sourcePath, CancellationToken cancellationToken);

    bool IsStoredImage(string profilePath, int presetNumber, string imagePath);
}
