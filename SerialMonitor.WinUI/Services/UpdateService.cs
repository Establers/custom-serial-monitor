using System.Net.Http;
using System.Text.Json;

namespace SerialMonitor.WinUI.Services;

public sealed record AppRelease(string Tag, Version Version, Uri Page);

public sealed class UpdatePreferences
{
    public bool Automatic { get; set; } = true;
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public string? SkippedVersion { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public string? LastKnownTag { get; set; }
}

// Null means unchanged. Never send a window's complete stale snapshot to disk.
public sealed record UpdatePreferenceChange(
    bool? Automatic = null,
    string? SkippedVersion = null,
    DateTimeOffset? LastAttemptUtc = null,
    DateTimeOffset? LastSuccessUtc = null,
    string? LastKnownTag = null)
{
    public void ApplyTo(UpdatePreferences latest)
    {
        if (Automatic is { } automatic) latest.Automatic = automatic;
        if (SkippedVersion is not null) latest.SkippedVersion = SkippedVersion;
        if (LastAttemptUtc is { } attempt && (latest.LastAttemptUtc is null || attempt >= latest.LastAttemptUtc))
            latest.LastAttemptUtc = attempt;
        // Keep a result and its timestamp together; late older results cannot roll the cache back.
        if (LastSuccessUtc is { } success && LastKnownTag is not null &&
            (latest.LastSuccessUtc is null || success >= latest.LastSuccessUtc))
        {
            latest.LastSuccessUtc = success;
            latest.LastKnownTag = LastKnownTag;
        }
    }
}

public interface IUpdateService
{
    Task<UpdatePreferences> LoadAsync(CancellationToken token);
    Task<UpdatePreferences> ApplyAsync(UpdatePreferenceChange change, CancellationToken token);
    Task<AppRelease> CheckAsync(CancellationToken token);
}

public sealed class UpdateService : IUpdateService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 };
    private static readonly TimeSpan StorageTimeout = TimeSpan.FromSeconds(3);
    private readonly string _path;
    private readonly HttpClient _client = Client;

    public UpdateService(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "updates.json"));

    internal UpdateService(string path, HttpClient client) : this(path) => _client = client;

    // Task.Run is intentional: opening, replacing and deleting local files have synchronous
    // portions even with async streams. None of them may run on the UI dispatcher.
    public Task<UpdatePreferences> LoadAsync(CancellationToken token) => RunStorageAsync(ReadAsync, token);

    public Task<UpdatePreferences> ApplyAsync(UpdatePreferenceChange change, CancellationToken token) =>
        RunStorageAsync(async ct =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // The OS releases this exclusive handle even if another process crashes.
            // Keep the lock file: deleting it would allow two different lock identities.
            using var fileLock = await AcquireLockAsync(ct).ConfigureAwait(false);
            var latest = await ReadAsync(ct).ConfigureAwait(false);
            change.ApplyTo(latest);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(output, latest, cancellationToken: ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, _path, overwrite: true);
                return latest;
            }
            finally
            {
                // Cleanup must not hide the original write/timeout error.
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }, token);

    private static async Task<UpdatePreferences> RunStorageAsync(
        Func<CancellationToken, Task<UpdatePreferences>> action, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(StorageTimeout);
        try
        {
            return await Task.Run(() => action(deadline.Token), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Update preference storage timed out.");
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(40, token).ConfigureAwait(false);
            }
        }
    }

    private async Task<UpdatePreferences> ReadAsync(CancellationToken token)
    {
        try
        {
            await using var input = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (input.Length > 16_384) throw new InvalidDataException("Update preferences are too large.");
            return await JsonSerializer.DeserializeAsync<UpdatePreferences>(input, cancellationToken: token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Invalid update preferences.");
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    public Task<AppRelease> CheckAsync(CancellationToken token) => Task.Run(async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/Establers/custom-serial-monitor/releases/latest");
        request.Headers.UserAgent.ParseAdd("SerialMonitor-UpdateCheck/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
    }, token);

    public static AppRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("The release is not a stable release.");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag) ?? throw new InvalidDataException("Unrecognized release version.");
        // Construct a repository-scoped URL rather than launching an arbitrary API field.
        return CreateRelease(tag, version);
    }

    public static AppRelease CreateRelease(string tag, Version version) =>
        new(tag, version, new Uri("https://github.com/Establers/custom-serial-monitor/releases/tag/" + Uri.EscapeDataString(tag)));

    public static Version? ParseVersion(string text)
    {
        var value = text.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(value, out var version) || version.Build < 0) return null;
        return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }

    public static bool IsAutomaticCheckDue(UpdatePreferences preferences, DateTimeOffset now) =>
        preferences.Automatic && (preferences.LastAttemptUtc is not { } last || now < last || now - last >= TimeSpan.FromDays(1));
}
