using System.Text.Json;

namespace SerialMonitor.WinUI.Services;

// Store aliases independently of default.json. Each write merges one port into
// the latest file under an OS lock, including when another app is running.
public sealed class PortAliasService : IPortAliasService
{
    public const int MaximumAliasLength = 64;
    private const int MaximumPortCount = 512;
    private const int MaximumStorageBytes = 256 * 1024;
    private static readonly TimeSpan StorageTimeout = TimeSpan.FromSeconds(3);
    private readonly string _path;

    public PortAliasService(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SerialMonitor", "port-aliases.json"));

    public Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken) =>
        RunStorageAsync<IReadOnlyDictionary<string, string>>(
            async token => await ReadAsync(token).ConfigureAwait(false), cancellationToken);

    public Task SetAsync(string portName, string alias, CancellationToken cancellationToken)
    {
        var port = NormalizePortName(portName);
        var name = NormalizeAlias(alias);
        return RunStorageAsync(async token =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var fileLock = await AcquireLockAsync(token).ConfigureAwait(false);
            var latest = await ReadAsync(token).ConfigureAwait(false);
            if (name.Length == 0) latest.Remove(port);
            else latest[port] = name;
            if (latest.Count > MaximumPortCount)
                throw new InvalidDataException("Too many saved port names.");

            var data = JsonSerializer.SerializeToUtf8Bytes(latest);
            if (data.Length > MaximumStorageBytes)
                throw new InvalidDataException("Saved port names are too large.");
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await output.WriteAsync(data, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return true;
        }, cancellationToken);
    }

    internal static string NormalizePortName(string? portName)
    {
        var port = portName?.Trim().ToUpperInvariant() ?? string.Empty;
        if (port.Length is 0 or > 128 || HasInvalidTitleCharacters(port))
            throw new ArgumentException("Invalid port name.", nameof(portName));
        return port;
    }

    internal static string NormalizeAlias(string? alias)
    {
        var name = alias?.Trim() ?? string.Empty;
        if (name.Length > MaximumAliasLength || HasInvalidTitleCharacters(name))
            throw new ArgumentException("Use at most 64 valid characters on one line.", nameof(alias));
        return name;
    }

    private static bool HasInvalidTitleCharacters(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            // Reject title control/bidi marks and malformed UTF-16. Valid emoji
            // pairs and joiners remain allowed and round-trip through JSON.
            if (char.IsControl(value) || value is '\u2028' or '\u2029' or '\u061C' or '\u200E' or '\u200F' ||
                value is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
                return true;
            if (char.IsHighSurrogate(value))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[++index])) return true;
            }
            else if (char.IsLowSurrogate(value)) return true;
        }
        return false;
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken token)
    {
        try
        {
            await using var input = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (input.Length > MaximumStorageBytes)
                throw new InvalidDataException("Saved port names are too large.");
            // Parse properties explicitly: dictionary deserialization silently
            // accepts duplicate keys by overwriting the earlier value.
            using var saved = await JsonDocument.ParseAsync(input,
                new JsonDocumentOptions { MaxDepth = 4 }, token).ConfigureAwait(false);
            if (saved.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid saved port names.");
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in saved.RootElement.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                if (normalized.Count >= MaximumPortCount)
                    throw new InvalidDataException("Too many saved port names.");
                if (entry.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Invalid saved port name.");
                var port = NormalizePortName(entry.Name);
                var name = NormalizeAlias(entry.Value.GetString());
                if (!normalized.TryAdd(port, name))
                    throw new InvalidDataException("Duplicate saved port name.");
            }
            return normalized;
        }
        catch (FileNotFoundException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (DirectoryNotFoundException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // Keep the lock file so all launches retain the same lock identity.
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(40, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> RunStorageAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StorageTimeout);
        try
        {
            // File open/replace have synchronous portions; keep them off the UI thread.
            return await Task.Run(() => action(deadline.Token), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Saving or loading port names timed out.");
        }
    }
}
