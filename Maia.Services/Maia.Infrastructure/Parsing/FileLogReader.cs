using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Parsing;

public sealed class FileLogReader(ILogger<FileLogReader> logger) : ILogReader
{
    public async Task<string> ReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Log file not found: {Path}", path);
                return string.Empty;
            }
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read log file: {Path}", path);
            return string.Empty;
        }
    }
}
