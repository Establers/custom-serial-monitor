using System.Text;
using SerialMonitor.Cli;

namespace SerialMonitor.Core.Tests;

public sealed class CliCommandInputTests
{
    [Fact]
    public void FileCommandPreservesQuotesUnicodeWhitespaceAndNewlines()
    {
        var path = Path.GetTempFileName();
        try
        {
            var text = "  set \"이름=$value`literal\"\r\nsecond line\n";
            File.WriteAllText(path, text, new UTF8Encoding(true));
            var args = new Dictionary<string, object?>();
            CliCommandInput.AddArguments(new Dictionary<string, string> { ["text-file"] = path }, args);
            Assert.Equal(text, args["command"]);
            Assert.Equal("text", args["txMode"]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16385)]
    public void InvalidFileLengthIsRejectedBeforeSending(int length)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, new string('x', length));
            Assert.Throws<ArgumentException>(() => CliCommandInput.ReadTextFile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConflictingCommandInputsAreRejectedWithoutOpeningTheFile()
    {
        Assert.Throws<ArgumentException>(() => CliCommandInput.AddArguments(
            new Dictionary<string, string> { ["text"] = "status", ["text-file"] = "does-not-exist.txt" },
            new Dictionary<string, object?>()));
    }
}
