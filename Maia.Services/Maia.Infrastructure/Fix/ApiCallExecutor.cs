using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Fix;

/// <summary>
/// Executes a fix by calling an HTTP endpoint.
/// ActionPayload = URL; placeholders resolved via IPlaceholderResolver.
/// Example: http://jobs.internal/api/retry/{failureId}
/// Per-step timeout: <see cref="ExecutorTimeouts.Default"/> (60s).
/// </summary>
public sealed class ApiCallExecutor(
    IHttpClientFactory       httpClientFactory,
    IPlaceholderResolver     resolver,
    ILogger<ApiCallExecutor> logger) : IFixActionExecutor
{
    public FixActionType ActionType => FixActionType.ApiCall;

    public async Task<FixActionResult> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            logger.LogError("ApiCallExecutor: ActionPayload (URL) is required for Failure {FailureId}",
                recommendation.FailureId);
            return false;
        }

        var url = await resolver.ResolveAsync(payload, recommendation, ct);

        // Per-step hard cap. HttpClient defaults to 100s, longer than our
        // step contract. The linked CTS guarantees we abandon the request
        // by 60s even if the upstream server is just slow.
        using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Default);

        try
        {
            var client   = httpClientFactory.CreateClient("FixEngine");
            var response = await client.PostAsync(url, content: null, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "ApiCallExecutor: POST {Url} succeeded ({StatusCode}) for Failure {FailureId}",
                    url, (int)response.StatusCode, recommendation.FailureId);
                return true;
            }

            logger.LogWarning(
                "ApiCallExecutor: POST {Url} returned {StatusCode} for Failure {FailureId}",
                url, (int)response.StatusCode, recommendation.FailureId);
            return FixActionResult.Fail($"Endpoint returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "ApiCallExecutor: POST {Url} timed out after {Seconds}s for Failure {FailureId}",
                url, ExecutorTimeouts.Default.TotalSeconds, recommendation.FailureId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ApiCallExecutor: HTTP call to {Url} failed for Failure {FailureId}",
                url, recommendation.FailureId);
            return FixActionResult.Fail(ex.Message);
        }
    }
}
