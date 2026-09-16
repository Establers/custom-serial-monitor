using System.Text;
using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class CommandSequenceFileServiceTests
{
    private const string Valid = """
        {"FormatVersion":1,"Name":"상태 확인","RepeatCount":2,"Steps":[
          {"CommandText":"status","LineEndingMode":"Crlf","DelayAfterMs":500,"Comment":"설명"}]}
        """;

    [Fact]
    public void ExportRoundTrip_PreservesDataAndIncludesManualOnlyAsMetadata()
    {
        using var input = JsonDocument.Parse(Valid);
        var original = CommandSequenceFileService.Parse(input.RootElement);
        var json = CommandSequenceFileService.Serialize(original);
        using var output = JsonDocument.Parse(json);
        Assert.NotEmpty(output.RootElement.GetProperty("_manual").EnumerateArray());
        Assert.DoesNotContain("DisplayName", json);
        var restored = CommandSequenceFileService.Parse(output.RootElement);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(2, restored.RepeatCount);
        var step = Assert.Single(restored.Steps);
        Assert.Equal("status", step.CommandText);
        Assert.Equal(TxLineEndingMode.Crlf, step.LineEndingMode);
        Assert.Equal(500, step.DelayAfterMs);
        Assert.Equal("설명", step.Comment);
    }

    [Theory]
    [InlineData("\"FormatVersion\":1", "\"FormatVersion\":2")]
    [InlineData("\"FormatVersion\":1,", "")]
    [InlineData("\"RepeatCount\":2", "\"RepeatCount\":0")]
    [InlineData("\"RepeatCount\":2", "\"RepeatCount\":10000")]
    [InlineData("\"RepeatCount\":2", "\"RepeatCount\":\"2\"")]
    [InlineData("\"RepeatCount\":2", "\"RepeatCount\":2.0")]
    [InlineData("\"RepeatCount\":2", "\"RepeatCount\":2,\"RepeatCount\":3")]
    [InlineData("\"RepeatCount\":2", "\"repeatCount\":2")]
    [InlineData("\"CommandText\":\"status\"", "\"CommandText\":\" \"")]
    [InlineData("\"CommandText\":\"status\"", "\"CommandText\":null")]
    [InlineData("\"CommandText\":\"status\"", "\"CommandText\":\"status\",\"WaitFor\":\"OK\"")]
    [InlineData("\"LineEndingMode\":\"Crlf\"", "\"LineEndingMode\":\"Global\"")]
    [InlineData("\"LineEndingMode\":\"Crlf\"", "\"LineEndingMode\":3")]
    [InlineData("\"LineEndingMode\":\"Crlf\",", "")]
    [InlineData("\"DelayAfterMs\":500", "\"DelayAfterMs\":-1")]
    [InlineData("\"DelayAfterMs\":500", "\"DelayAfterMs\":600001")]
    [InlineData("\"Comment\":\"설명\"", "\"Comment\":false")]
    [InlineData("\"FormatVersion\":1", "\"_manual\":{},\"FormatVersion\":1")]
    public void InvalidSchema_IsRejected(string from, string to)
    {
        using var input = JsonDocument.Parse(Valid.Replace(from, to));
        Assert.Throws<InvalidDataException>(() => CommandSequenceFileService.Parse(input.RootElement));
    }

    [Fact]
    public async Task Load_ReturnsNormalizedAndNumberedSteps()
    {
        var path = Path.GetTempFileName();
        try
        {
            var json = Valid.Replace("상태 확인", "  상태 확인  ")
                .Replace("\"status\"", "\"  status  \",\"Name\":\"  \"")
                .Replace("\"설명\"", "\"  설명  \"");
            await File.WriteAllTextAsync(path, json);
            var sequence = Assert.Single(await new CommandSequenceFileService().LoadAsync([path], CancellationToken.None));
            Assert.Equal("상태 확인", sequence.Name);
            var step = Assert.Single(sequence.Steps);
            Assert.Equal("status", step.CommandText);
            Assert.Null(step.Name);
            Assert.Equal("설명", step.Comment);
            Assert.Equal(1, step.StepNumber);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BatchStepLimit_AcceptsBoundaryAndRejectsCombinedOverflow()
    {
        var a = Path.GetTempFileName();
        var b = Path.GetTempFileName();
        try
        {
            const string step = """{"CommandText":"status","LineEndingMode":null,"DelayAfterMs":0}""";
            string MakeJson(string name, int count) =>
                "{\"FormatVersion\":1,\"Name\":\"" + name + "\",\"RepeatCount\":1,\"Steps\":[" + string.Join(",", Enumerable.Repeat(step, count)) + "]}";
            var service = new CommandSequenceFileService();
            await File.WriteAllTextAsync(a, MakeJson("A", CommandSequenceFileService.MaxBatchSteps / 2));
            await File.WriteAllTextAsync(b, MakeJson("B", CommandSequenceFileService.MaxBatchSteps / 2));
            var loaded = await service.LoadAsync([a, b], CancellationToken.None);
            Assert.Equal(CommandSequenceFileService.MaxBatchSteps, loaded.Sum(sequence => sequence.Steps.Count));
            Assert.All(loaded, sequence => Assert.Equal(sequence.Steps.Count, sequence.Steps[^1].StepNumber));
            await File.WriteAllTextAsync(b, MakeJson("B", CommandSequenceFileService.MaxBatchSteps / 2 + 1));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync([a, b], CancellationToken.None));
            Assert.Contains("total steps", error.Message);
            Assert.Contains(Path.GetFileName(b), error.Message);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public async Task BatchByteLimit_AcceptsBoundaryAndRejectsCombinedOverflow()
    {
        var paths = Enumerable.Range(0, 9).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            for (var i = 0; i < paths.Length; i++)
            {
                var json = $"{{\"FormatVersion\":1,\"Name\":\"{i}\",\"RepeatCount\":1,\"Steps\":[]}}";
                await File.WriteAllTextAsync(paths[i], json.PadRight(CommandSequenceFileService.MaxFileBytes), new UTF8Encoding(false));
            }
            var service = new CommandSequenceFileService();
            Assert.Equal(8, (await service.LoadAsync(paths[..8], CancellationToken.None)).Count);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(paths, CancellationToken.None));
            Assert.Contains("total input", error.Message);
        }
        finally { foreach (var path in paths) File.Delete(path); }
    }

    [Fact]
    public void Parse_ObservesCancellationBeforePreparingSteps()
    {
        using var document = JsonDocument.Parse(Valid);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CommandSequenceFileService.Parse(document.RootElement, cancelled.Token));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Cr")]
    [InlineData("Lf")]
    [InlineData("Crlf")]
    [InlineData(null)]
    public void AllSupportedEndings_RoundTrip(string? ending)
    {
        using var input = JsonDocument.Parse(Valid.Replace("\"Crlf\"", JsonSerializer.Serialize(ending)));
        var sequence = CommandSequenceFileService.Parse(input.RootElement);
        using var output = JsonDocument.Parse(CommandSequenceFileService.Serialize(sequence));
        Assert.Equal(sequence.Steps[0].LineEndingMode, CommandSequenceFileService.Parse(output.RootElement).Steps[0].LineEndingMode);
    }

    [Fact]
    public async Task MultipleFiles_BomAndExportAreSupported_AndBadBatchFails()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "a.json");
        var b = Path.Combine(folder, "b.json");
        try
        {
            var service = new CommandSequenceFileService();
            await File.WriteAllTextAsync(a, Valid, new UTF8Encoding(true));
            await File.WriteAllTextAsync(b, Valid.Replace("상태 확인", "Second"));
            var loaded = await service.LoadAsync([a, b], CancellationToken.None);
            Assert.Equal(2, loaded.Count);
            await service.ExportAsync(b, loaded[1], CancellationToken.None);
            Assert.Equal("Second", Assert.Single(await service.LoadAsync([b], CancellationToken.None)).Name);
            await File.WriteAllTextAsync(b, Valid);
            Assert.Contains("b.json", (await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync([a, b], CancellationToken.None))).Message);
            foreach (var invalid in new[] { "{}", "[]", Valid.Replace("500", "500,"), "// comment\n" + Valid, new string(' ', CommandSequenceFileService.MaxFileBytes + 1) })
            {
                await File.WriteAllTextAsync(b, invalid);
                await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync([a, b], CancellationToken.None));
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync([a], cancelled.Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(Enumerable.Repeat(a, 101).ToArray(), CancellationToken.None));
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
            Directory.Delete(folder);
        }
    }
}
