using Maia.API.Middleware;
using Maia.Application.Classification;
using Maia.Application.Maintenance;
using Maia.Application.Pipeline;
using Maia.Application.Remediation;
using Maia.Application.Security;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;

namespace Maia.API.Extensions;

/// <summary>
/// Composition root for Application-layer use cases.
/// Infrastructure + domain services are wired in Infrastructure.Extensions.ServiceCollectionExtensions.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IClassifyJobsUseCase,         ClassifyJobsUseCase>();
        services.AddScoped<IReclassifyOrphanedFailuresUseCase, ReclassifyOrphanedFailuresUseCase>();
        services.AddScoped<IGenerateSuggestionsUseCase,  GenerateSuggestionsUseCase>();
        services.AddScoped<IExecuteFixesUseCase,         ExecuteFixesUseCase>();
        services.AddScoped<IDirectoryPipelineUseCase,    DirectoryPipelineUseCase>();
        services.AddScoped<IScanHistoryRetentionService, ScanHistoryRetentionService>();
        services.AddScoped<IAuthService,                 AuthService>();

        return services;
    }

    public static IServiceCollection AddGlobalExceptionHandling(this IServiceCollection services)
    {
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        return services;
    }
}
