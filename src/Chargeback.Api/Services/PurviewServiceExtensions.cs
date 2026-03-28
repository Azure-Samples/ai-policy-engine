using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI.Purview;

namespace Chargeback.Api.Services;

/// <summary>
/// Configures the Microsoft Agent Framework (MAF) Purview integration
/// for DLP policy validation and audit emission on AI interactions.
///
/// This service is a log receiver, not an AI agent — so we use Purview's
/// Content Activities API for audit emission rather than the chat middleware pattern.
/// The PurviewClient is registered for direct use by the <see cref="PurviewAuditService"/>.
///
/// Reference: https://github.com/microsoft/agent-framework/tree/main/dotnet/src/Microsoft.Agents.AI.Purview
/// </summary>
public static class PurviewServiceExtensions
{
    /// <summary>
    /// Adds Purview services to the DI container for policy validation and audit emission.
    /// </summary>
    public static IServiceCollection AddPurviewServices(this IServiceCollection services, IConfiguration configuration)
    {
        var purviewClientAppId = configuration["PURVIEW_CLIENT_APP_ID"];

        if (string.IsNullOrWhiteSpace(purviewClientAppId))
        {
            // Purview not configured — register no-op audit service
            services.AddSingleton<IPurviewAuditService, NoOpPurviewAuditService>();
            return services;
        }

        var settings = new PurviewSettings(configuration["PURVIEW_APP_NAME"] ?? "Chargeback API")
        {
            AppVersion = "1.0.0",
            TenantId = configuration["PURVIEW_TENANT_ID"],
            PurviewAppLocation = !string.IsNullOrWhiteSpace(configuration["PURVIEW_APP_LOCATION"])
                ? new PurviewAppLocation(PurviewLocationType.Uri, configuration["PURVIEW_APP_LOCATION"]!)
                : null,
            IgnoreExceptions = bool.TryParse(configuration["PURVIEW_IGNORE_EXCEPTIONS"], out var ignore) && ignore,
            PendingBackgroundJobLimit = int.TryParse(configuration["PURVIEW_BACKGROUND_JOB_LIMIT"], out var limit) ? limit : 100,
            MaxConcurrentJobConsumers = int.TryParse(configuration["PURVIEW_MAX_CONCURRENT_CONSUMERS"], out var consumers) ? consumers : 10,
        };

        TokenCredential credential = new DefaultAzureCredential();

        services.AddSingleton(settings);
        services.AddSingleton(credential);
        services.AddSingleton<IPurviewAuditService, PurviewAuditService>();

        return services;
    }
}
