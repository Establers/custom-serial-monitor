using System.Text;
using SerialMonitor.Cli;

namespace SerialMonitor.Core.Tests;

public sealed class CliTextFileIntegrityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "SerialTextIntegrity-" + Guid.NewGuid().ToString("N") + ".txt");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf8FilePreservesWhitespaceControlCharactersAndUnicode(bool bom)
    {
        const string text = " \t\0\u0001\u001b\"\\가🙂\r\n ";
        File.WriteAllText(_path, text, new UTF8Encoding(bom, true));
        Assert.Equal(text, CliCommandInput.ReadTextFile(_path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NonUtf8BomCannotSilentlyChangeTheCommand(bool utf32, bool bigEndian)
    {
        Encoding encoding = utf32 ? new UTF32Encoding(bigEndian, true) : new UnicodeEncoding(bigEndian, true);
        File.WriteAllText(_path, "status", encoding);
        Assert.Throws<DecoderFallbackException>(() => CliCommandInput.ReadTextFile(_path));
    }

    [Fact]
    public void InvalidUtf16CommandCannotBeReplacedDuringJsonSerialization()
    {
        var flags = new Dictionary<string, string> { ["text"] = "set \ud800 value" };
        Assert.Throws<EncoderFallbackException>(() => CliCommandInput.AddArguments(flags, new Dictionary<string, object?>()));
    }

    public void Dispose() => File.Delete(_path);
}
