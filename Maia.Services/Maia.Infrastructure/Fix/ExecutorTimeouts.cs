namespace Maia.Infrastructure.Fix;

/// <summary>
/// Per-executor / per-step timeout policy. Centralised so changing the
/// default is one edit, not seven. Each executor honours both the linked
/// <see cref="CancellationToken"/> from the caller AND this hard wall-clock
/// cap — whichever fires first wins.
///
/// Rationale for the default: the worker's per-job lease is the next layer
/// up (FS=300s, DB=1800s, ApiEndpoint=60s). A per-step cap of 60s lets a
/// composite of up to ~5 SQL/script steps complete within the FS lease
/// without contention; longer-running shapes (multi-statement SQL doing
/// real work, batch scripts) should override locally rather than tune this
/// default upward. <see cref="ScriptExecutor"/> keeps its 120s cap because
/// shell scripts are legitimately longer-running than SQL or HTTP calls.
/// </summary>
internal static class ExecutorTimeouts
{
    /// <summary>Default per-step timeout for SQL, HTTP, file-copy steps.
    /// Applied via <see cref="CancellationTokenSource.CancelAfter(System.TimeSpan)"/>
    /// on a linked CTS so cancellation propagates correctly.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(60);

    /// <summary>Shell / PowerShell scripts get more headroom — they often
    /// chain real work (file processing, external tool invocations). Matches
    /// the historical <see cref="ScriptExecutor"/> value.</summary>
    public static readonly TimeSpan Script = TimeSpan.FromSeconds(120);

    /// <summary>Convenience: build a linked CTS that cancels on either the
    /// caller's token OR after <paramref name="timeout"/>. Caller MUST
    /// dispose the returned source.</summary>
    public static CancellationTokenSource LinkedWithTimeout(CancellationToken outer, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        return cts;
    }
}
