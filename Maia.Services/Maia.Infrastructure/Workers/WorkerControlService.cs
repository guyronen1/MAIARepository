using Maia.Core.Interfaces;

namespace Maia.Infrastructure.Workers;

/// <summary>
/// Thread-safe in-memory pause/resume flag. Registered as a singleton so both
/// the MonitoringWorker (reader) and the AdminController (writer) share the
/// same instance. Uses an interlocked int instead of a lock for minimal overhead
/// on the hot worker path.
/// </summary>
public sealed class WorkerControlService : IWorkerControlService
{
    // 0 = running, 1 = paused
    private volatile int _paused;

    public bool IsPaused => _paused == 1;

    public void Pause()  => Interlocked.Exchange(ref _paused, 1);
    public void Resume() => Interlocked.Exchange(ref _paused, 0);
}
