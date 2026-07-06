using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Fix;

/// <summary>
/// Copies a file from SOURCE to DEST. Payload format: "SOURCE|DEST".
/// Both halves go through IPlaceholderResolver, but SOURCE requires
/// {sourceFilePath} to resolve to a non-empty value when it appears in
/// the template — that's the placeholder operators are most likely to
/// mis-configure (forgetting InputPathPattern on FS rules or
/// FilePathColumn on DB rules). The resolver throws a specific error
/// pointing at the fix in that case.
///
/// Behaviours (locked in spec):
///   - Source missing on disk → step fails
///   - Destination exists → overwrite (atomic via .tmp + rename)
///   - UNC paths supported natively (NTFS perms apply)
///   - Destination directory auto-created if missing
/// </summary>
public sealed class CopyFileExecutor(
    IPlaceholderResolver       resolver,
    ILogger<CopyFileExecutor>  logger) : IFixActionExecutor
{
    private static readonly string[] SourceRequiredPlaceholders = ["sourceFilePath"];

    public FixActionType ActionType => FixActionType.CopyFile;

    public async Task<FixActionResult> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            logger.LogError(
                "CopyFileExecutor: ActionPayload is required for Failure {FailureId} " +
                "(expected 'SOURCE|DEST')",
                recommendation.FailureId);
            return false;
        }

        // Split on FIRST '|' so paths containing '|' literally (rare; NTFS
        // forbids '|' anyway) don't break parsing.
        var pipe = payload.IndexOf('|');
        if (pipe <= 0 || pipe == payload.Length - 1)
        {
            logger.LogError(
                "CopyFileExecutor: ActionPayload malformed for Failure {FailureId} " +
                "(expected 'SOURCE|DEST', got '{Payload}')",
                recommendation.FailureId, payload);
            return false;
        }
        var sourceTemplate = payload[..pipe].Trim();
        var destTemplate   = payload[(pipe + 1)..].Trim();

        string sourcePath, destPath;
        try
        {
            // SOURCE: enforce that {sourceFilePath} is non-empty if used.
            // DEST is non-strict — operators may use a fully-literal dest path.
            sourcePath = await resolver.ResolveOrThrowAsync(
                sourceTemplate, recommendation, SourceRequiredPlaceholders, ct);
            destPath   = await resolver.ResolveAsync(destTemplate, recommendation, ct);
        }
        catch (PlaceholderUnresolvedException ex)
        {
            logger.LogError(
                "CopyFileExecutor: {Message} (Failure {FailureId})",
                ex.Message, recommendation.FailureId);
            return false;
        }

        if (!File.Exists(sourcePath))
        {
            logger.LogError(
                "CopyFileExecutor: source not found '{Source}' for Failure {FailureId}",
                sourcePath, recommendation.FailureId);
            return FixActionResult.Fail($"Source file not found: {sourcePath}");
        }

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            try { Directory.CreateDirectory(destDir); }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "CopyFileExecutor: failed to create dest directory '{Dir}' for Failure {FailureId}",
                    destDir, recommendation.FailureId);
                return false;
            }
        }

        // Atomic + cancellable copy. File.Copy is synchronous and ignores
        // CancellationToken — once it starts a 5GB copy it runs to completion
        // regardless of operator cancellation or per-step timeout. Stream-
        // based copy with CopyToAsync(stream, ct) IS cancellable per-chunk
        // (default 81KB buffer), so cancellation propagates in ≤ one buffer.
        //
        // Atomic write: read source → write `.tmp` → File.Move to final
        // name. Readers never see a half-written destination. On cancel /
        // failure: tmp cleaned up so we don't litter `.tmp` files.
        using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Default);
        var tmpPath = destPath + ".tmp";
        try
        {
            await using (var src = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true))
            await using (var dst = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await src.CopyToAsync(dst, cts.Token);
            }
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmpPath, destPath);

            logger.LogInformation(
                "CopyFileExecutor: '{Source}' → '{Dest}' for Failure {FailureId}",
                sourcePath, destPath, recommendation.FailureId);
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Per-step timeout fired (not the outer cancellation).
            logger.LogWarning(
                "CopyFileExecutor: '{Source}' → '{Dest}' timed out after {Seconds}s for Failure {FailureId}",
                sourcePath, destPath, ExecutorTimeouts.Default.TotalSeconds, recommendation.FailureId);
            TryCleanupTmp(tmpPath);
            return false;
        }
        catch (Exception ex)
        {
            // Best-effort tmp cleanup. Swallow inside swallow — if we can't
            // delete the tmp the disk is in trouble anyway, that's a separate
            // problem the operator will notice from filesystem alerts.
            TryCleanupTmp(tmpPath);
            logger.LogError(ex,
                "CopyFileExecutor: copy '{Source}' → '{Dest}' failed for Failure {FailureId}",
                sourcePath, destPath, recommendation.FailureId);
            return FixActionResult.Fail(ex.Message);
        }
    }

    private static void TryCleanupTmp(string tmpPath)
    {
        if (!File.Exists(tmpPath)) return;
        try { File.Delete(tmpPath); } catch { /* swallow */ }
    }
}
