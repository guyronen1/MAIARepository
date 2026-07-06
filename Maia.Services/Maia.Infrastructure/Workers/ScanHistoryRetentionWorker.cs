using Maia.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maia.Infrastructure.Workers;

/// <summary>
/// Background scheduler: invokes <see cref="IScanHistoryRetentionService.SweepAsync"/>
/// on startup and then every <c>ScanHistory:CleanupIntervalHours</c> hours.
/// All sweep semantics (kill switch, batch size, delays, logging) live in the service.
/// </summary>
public sealed class ScanHistoryRetentionWorker(
    IServiceScopeFactory                  scopeFactory,
    IConfiguration                        config,
    ILogger<ScanHistoryRetentionWorker>   logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = config.GetValue("ScanHistory:CleanupIntervalHours", defaultValue: 6);
        logger.LogInformation("ScanHistoryRetentionWorker started (sweep every {Hours}h)", intervalHours);

        // Run immediately on startup so a freshly-deployed instance catches up
        await RunSweepAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ScanHistoryRetentionWorker: scheduler loop failed");
            }
        }
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope  = scopeFactory.CreateScope();
            var service      = scope.ServiceProvider.GetRequiredService<IScanHistoryRetentionService>();
            await service.SweepAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ScanHistoryRetentionWorker: sweep failed");
        }
    }
}
