using System.Diagnostics;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Fix;

/// <summary>
/// Executes a fix by running a shell or PowerShell script.
/// ActionPayload format: "executable [arguments]"
/// Placeholders are resolved via IPlaceholderResolver — see that interface
/// for the full token list ({failureId}, {sourceId}, {sourceFilePath},
/// {sourceLogPath}, {jobFolder}, {inputFolder}).
/// Examples:
///   powershell.exe -File C:\scripts\retry.ps1 -FailureId {failureId}
///   cmd.exe /c C:\scripts\fix.bat {sourceId} "{sourceFilePath}"
/// The process must exit with code 0 for the fix to be considered successful.
/// </summary>
public sealed class ScriptExecutor(
    IPlaceholderResolver    resolver,
    ILogger<ScriptExecutor> logger) : IFixActionExecutor
{
    public FixActionType ActionType => FixActionType.Script;

    public async Task<FixActionResult> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            logger.LogError("ScriptExecutor: ActionPayload (command) is required for Failure {FailureId}",
                recommendation.FailureId);
            return false;
        }

        var command = await resolver.ResolveAsync(payload, recommendation, ct);

        // Split "executable [rest of args]"
        var parts      = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var executable = parts[0];
        var arguments  = parts.Length > 1 ? parts[1] : string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName               = executable,
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Script);

            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode == 0)
            {
                logger.LogInformation(
                    "ScriptExecutor: '{Executable}' exited 0 for Failure {FailureId}",
                    executable, recommendation.FailureId);
                return true;
            }

            var stderr = await process.StandardError.ReadToEndAsync(ct);
            logger.LogWarning(
                "ScriptExecutor: '{Executable}' exited {ExitCode} for Failure {FailureId}. Stderr: {Stderr}",
                executable, process.ExitCode, recommendation.FailureId, stderr);
            return FixActionResult.Fail($"Script exited {process.ExitCode}. {stderr}".Trim());
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "ScriptExecutor: '{Executable}' timed out after {Seconds}s for Failure {FailureId}",
                executable, ExecutorTimeouts.Script.TotalSeconds, recommendation.FailureId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ScriptExecutor: Failed to launch '{Executable}' for Failure {FailureId}",
                executable, recommendation.FailureId);
            return FixActionResult.Fail(ex.Message);
        }
    }
}
