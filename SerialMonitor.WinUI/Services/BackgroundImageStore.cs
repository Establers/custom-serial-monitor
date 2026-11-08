namespace SerialMonitor.WinUI.Services;

// Each profile owns five slots outside the installation directory. Fixed names
// bound storage to five images per supported extension, even after replacements.
public sealed class BackgroundImageStore : IBackgroundImageStore
{
    public const long MaxImageBytes = 20L * 1024 * 1024;
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    public bool IsStoredImage(string profilePath, int presetNumber, string imagePath)
    {
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        return Extensions.Contains(extension) && string.Equals(
            Path.GetFullPath(imagePath), GetImagePath(profilePath, presetNumber, extension), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> ImportAsync(string profilePath, int presetNumber, string sourcePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!Extensions.Contains(extension))
            throw new InvalidDataException("Choose a PNG, JPG, JPEG or WebP background image.");

        var destination = GetImagePath(profilePath, presetNumber, extension);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsStoredImage(profilePath, presetNumber, sourcePath)) return destination;

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length is <= 0 or > MaxImageBytes)
            throw new InvalidDataException("Background images must be between 1 byte and 20 MB.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                // The length can change while reading a file on some filesystems.
                var buffer = new byte[64 * 1024];
                long copied = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    copied += count;
                    if (copied > MaxImageBytes) throw new InvalidDataException("Background images must not exceed 20 MB.");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (copied == 0) throw new InvalidDataException("The background image is empty.");
                await target.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string GetImagePath(string profilePath, int presetNumber, string extension)
    {
        if (presetNumber is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(presetNumber));
        return Path.Combine(Path.GetFullPath(profilePath) + ".backgrounds", $"preset-{presetNumber}{extension}");
    }
}
