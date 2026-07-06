namespace Maia.Core.Interfaces;

public interface ILogParser
{
    string[] ParseLog(string content);
    string? ExtractFirstError(string[] lines);
}
