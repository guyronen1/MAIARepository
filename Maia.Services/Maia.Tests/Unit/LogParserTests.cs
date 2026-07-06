using Maia.Infrastructure.Parsing;
using Xunit;

namespace Maia.Tests.Unit;

public class LogParserTests
{
    private readonly SimpleLogParser _sut = new();

    [Fact]
    public void ParseLog_SplitsOnLf()
    {
        var lines = _sut.ParseLog("line1\nline2\nline3");
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void ParseLog_SplitsOnCrLf_WindowsLineEndings()
    {
        var lines = _sut.ParseLog("line1\r\nline2\r\nline3");
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void ParseLog_EmptyString_ReturnsEmptyArray()
    {
        var lines = _sut.ParseLog(string.Empty);
        Assert.Empty(lines);
    }

    [Fact]
    public void ParseLog_SingleLine_ReturnsOneElement()
    {
        var lines = _sut.ParseLog("single line");
        Assert.Single(lines);
    }

    [Fact]
    public void ExtractFirstError_FindsErrorKeyword()
    {
        var lines = new[] { "Starting job", "ERROR: something failed", "Done" };
        Assert.Equal("ERROR: something failed", _sut.ExtractFirstError(lines));
    }

    [Fact]
    public void ExtractFirstError_FindsExceptionKeyword()
    {
        var lines = new[] { "INFO: start", "System.Exception: boom", "INFO: end" };
        Assert.Equal("System.Exception: boom", _sut.ExtractFirstError(lines));
    }

    [Fact]
    public void ExtractFirstError_ReturnsNull_WhenNoErrors()
    {
        var lines = new[] { "Starting", "Processing", "Done" };
        Assert.Null(_sut.ExtractFirstError(lines));
    }

    [Fact]
    public void ExtractFirstError_ReturnsNull_ForEmptyLog()
    {
        Assert.Null(_sut.ExtractFirstError([]));
    }

    [Fact]
    public void ExtractFirstError_IsCaseInsensitive()
    {
        var lines = new[] { "step 1", "FAILED: connection refused", "end" };
        Assert.Equal("FAILED: connection refused", _sut.ExtractFirstError(lines));
    }

    [Fact]
    public void LoadLogFileAndExtractErrors()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "OK line\nERROR: disk full\nDone");
        try
        {
            var content = File.ReadAllText(file);
            var lines   = _sut.ParseLog(content);
            var error   = _sut.ExtractFirstError(lines);
            Assert.Equal("ERROR: disk full", error);
        }
        finally { File.Delete(file); }
    }
}
