namespace Maia.Core.Interfaces;

public interface ILogReader
{
    Task<string> ReadAsync(string path, CancellationToken ct = default);
}
