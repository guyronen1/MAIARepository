namespace Maia.Core.Interfaces;

/// <summary>
/// Allows operators to pause and resume the MonitoringWorker scan loop at
/// runtime without restarting the process. The implementation is a singleton
/// that the worker polls on every idle/tick cycle.
/// </summary>
public interface IWorkerControlService
{
    /// <summary>True while the worker is paused — scans are suppressed.</summary>
    bool IsPaused { get; }

    void Pause();
    void Resume();
}
