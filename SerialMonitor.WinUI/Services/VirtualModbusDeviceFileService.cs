using System.Text;
using SerialMonitor.Core.Protocols.VirtualModbus;

namespace SerialMonitor.WinUI.Services;

public interface IVirtualModbusDeviceFileService
{
    Task<VirtualModbusDefinition> LoadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
    Task SaveAsync(string path, VirtualModbusDefinition definition, CancellationToken cancellationToken);
}

public sealed class VirtualModbusDeviceFileService : IVirtualModbusDeviceFileService
{
    public Task<VirtualModbusDefinition> LoadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            if (paths.Count is < 1 or > 32) throw new FormatException("Select 1–32 JSON files.");
            var devices = new List<VirtualModbusDeviceDefinition>();
            long total = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
                    using var buffer = new MemoryStream();
                    var bytes = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        if (buffer.Length + count > VirtualModbusJson.MaximumJsonBytes || (total += count) > 16 * 1024 * 1024)
                            throw new FormatException("JSON exceeds 16 MiB per file / import.");
                        buffer.Write(bytes, 0, count);
                    }
                    var json = new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
                    devices.AddRange(VirtualModbusJson.Parse(json).Devices);
                    if (devices.Count > VirtualModbusJson.MaximumDevices || devices.Sum(d => d.Registers.Count) > VirtualModbusJson.MaximumRegisters)
                        throw new FormatException("Import exceeds 32 devices / 8192 points.");
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or DecoderFallbackException)
                { throw new FormatException($"{Path.GetFileName(path)}: {ex.Message}", ex); }
            }
            var result = new VirtualModbusDefinition { SchemaVersion = 1, Devices = devices };
            new VirtualModbusDeviceStore().Add(result);
            return result;
        }, cancellationToken);

    public Task SaveAsync(string path, VirtualModbusDefinition definition, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var json = VirtualModbusJson.Serialize(definition);
        // A saved file must be accepted by the same importer.
        VirtualModbusJson.Parse(json);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, cancellationToken);
}
