using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal interface IInspectorLayoutStore
{
    Task<InspectorLayoutState> LoadAsync(CancellationToken token);
    Task SaveAsync(InspectorLayoutState state, CancellationToken token);
}

internal sealed class InspectorLayoutStore : IInspectorLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;

    // The override isolates native UI/restart fixtures from the user's layout.
    public InspectorLayoutStore(string? path = null) => _path = Path.GetFullPath(path ??
        Environment.GetEnvironmentVariable("SERIAL_MONITOR_LAYOUT_PATH") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "inspector-layout.json"));

    public Task<InspectorLayoutState> LoadAsync(CancellationToken token) => RunAsync(async ct =>
    {
        try
        {
            await using var input = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (input.Length > 16_384) throw new InvalidDataException("Inspector layout is too large.");
            return (await JsonSerializer.DeserializeAsync<InspectorLayoutState>(input, Options, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Invalid inspector layout.")).Normalize();
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }, token);

    public Task SaveAsync(InspectorLayoutState state, CancellationToken token) => RunAsync(async ct =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var lease = await AcquireLockAsync(ct).ConfigureAwait(false);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, state.Normalize(), Options, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }, token);

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

    private static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try { return await Task.Run(() => action(deadline.Token), deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Inspector layout storage timed out."); }
    }
}
