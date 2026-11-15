using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using SerialMonitor.Core.Modbus;

namespace SerialMonitor.WinUI.Services;

public sealed record ModbusDevicePreset(string Id, string Json, ModbusSensorDefinition Definition, bool IsBuiltIn = false)
{
    public string Label => Definition.Name + (IsBuiltIn ? " · 기본 예제" : "");
}

public sealed record ModbusDeviceLibraryResult(IReadOnlyList<ModbusDevicePreset> Items, string Warning);

public interface IModbusDeviceLibrary
{
    Task<ModbusDeviceLibraryResult> LoadAsync(CancellationToken token);
    Task<ModbusDevicePreset> SaveAsync(string json, ModbusDevicePreset? previous, CancellationToken token);
    Task DeleteAsync(ModbusDevicePreset preset, CancellationToken token);
}

// Each product is a standalone v1 JSON file. Built-ins stay in the application
// payload; user products are atomic files, independent of the serial profile.
public sealed class ModbusDeviceLibrary(string directory, string examplePath) : IModbusDeviceLibrary
{
    private const int MaximumProducts = 128;
    private const int MaximumBytes = 262144;
    internal static readonly JsonSerializerOptions JsonOptions = new(ModbusSensorDefinition.JsonOptions)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "modbus-devices");
    private string CatalogPath => Path.Combine(directory, "catalog");

    public Task<ModbusDeviceLibraryResult> LoadAsync(CancellationToken token) => Task.Run(async () =>
    {
        var items = new List<ModbusDevicePreset>();
        var warnings = new List<string>();
        try
        {
            var json = await ReadJsonFileAsync(examplePath, token);
            items.Add(new("ee071", json, ModbusSensorDefinition.Parse(json), true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("EE071 예제를 읽지 못했어요: " + ex.Message); }
        if (Directory.Exists(directory))
        {
            var files = Directory.EnumerateFiles(directory, "*.json").Take(MaximumProducts + 1).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length > MaximumProducts) warnings.Add($"제품 목록은 {MaximumProducts}개까지 표시해요.");
            foreach (var file in files.Take(MaximumProducts))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("목록 파일 이름이 올바르지 않아요.");
                    var json = await ReadJsonFileAsync(file, token);
                    items.Add(new(id, json, ModbusSensorDefinition.Parse(json)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
        return new ModbusDeviceLibraryResult(items, string.Join("\n", warnings));
    }, token);

    public Task<ModbusDevicePreset> SaveAsync(string json, ModbusDevicePreset? previous, CancellationToken token) => Task.Run(async () =>
    {
        if (previous?.IsBuiltIn == true) throw new InvalidOperationException("기본 예제는 복제해서 편집하세요.");
        var definition = ModbusSensorDefinition.Parse(json);
        var canonical = JsonSerializer.Serialize(definition, JsonOptions);
        if (Encoding.UTF8.GetByteCount(canonical) > MaximumBytes) throw new IOException("제품 JSON은 256 KiB 이하로 저장하세요.");
        await using var lease = await ProfileFileLease.AcquireAsync(CatalogPath, token);
        if (previous is null && Directory.EnumerateFiles(directory, "*.json").Take(MaximumProducts).Count() >= MaximumProducts)
            throw new IOException($"제품은 {MaximumProducts}개까지 저장할 수 있어요.");
        var id = previous?.Id ?? Guid.NewGuid().ToString("N");
        var path = ProductPath(id);
        if (previous is not null) await VerifyUnchanged(previous, path, token);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, canonical, new UTF8Encoding(false), token);
            await ProfileFileCommit.CommitAsync(temporary, path, path + ".bak", token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new ModbusDevicePreset(id, canonical, definition);
    }, token);

    public Task DeleteAsync(ModbusDevicePreset preset, CancellationToken token) => Task.Run(async () =>
    {
        if (preset.IsBuiltIn) throw new InvalidOperationException("기본 예제는 삭제할 수 없어요.");
        await using var lease = await ProfileFileLease.AcquireAsync(CatalogPath, token);
        var path = ProductPath(preset.Id);
        await VerifyUnchanged(preset, path, token);
        File.Delete(path);
    }, token);

    private string ProductPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("제품 ID가 올바르지 않아요.");
        return Path.Combine(directory, id + ".json");
    }

    private static async Task VerifyUnchanged(ModbusDevicePreset previous, string path, CancellationToken token)
    {
        if (!File.Exists(path) || await ReadJsonFileAsync(path, token) != previous.Json)
            throw new IOException("다른 창에서 이 제품을 변경했어요. 제품 관리 → 목록 새로 고침 후 다시 편집하세요.");
    }

    public static async Task<string> ReadJsonFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaximumBytes) throw new IOException("JSON 파일은 256 KiB 이하로 선택하세요.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var buffer = new char[MaximumBytes + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
        if (count > MaximumBytes) throw new IOException("JSON 파일이 너무 커요.");
        return new string(buffer, 0, count);
    }
}
