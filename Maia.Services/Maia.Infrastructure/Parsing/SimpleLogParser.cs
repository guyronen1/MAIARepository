using Maia.Core.Interfaces;

namespace Maia.Infrastructure.Parsing;

public sealed class SimpleLogParser : ILogParser
{
    private static readonly string[] ErrorKeywords = ["error", "exception", "failed"];

    public string[] ParseLog(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return content
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
    }

    public string? ExtractFirstError(string[] lines)
        => lines.FirstOrDefault(l =>
            ErrorKeywords.Any(kw =>
                l.Contains(kw, StringComparison.OrdinalIgnoreCase)));
}
