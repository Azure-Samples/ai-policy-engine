using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI.Purview;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Configures the Microsoft Purview integration for DLP policy validation and audit emission.
///
/// This service is a log receiver, not an AI agent — so we use Purview's Content Activities
/// API for audit emission rather than the chat middleware pattern.
///
/// DI strategy: a single <em>base</em> <see cref="PurviewSettings"/> instance is registered
/// (without an <c>AppName</c> lock-in). <see cref="PurviewAuditService"/> creates a per-event
/// copy, overriding <c>AppName</c> with the emitting client's display name so that Purview
/// groups activity under the correct application identity.
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

        // "Base" settings — AppName intentionally left as the service-level fallback.
        // PurviewAuditService overrides AppName per-event with the client's display name.
        var baseSettings = new PurviewSettings(configuration["PURVIEW_APP_NAME"] ?? "AI Policy Engine API")
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

        var blockEnabled = bool.TryParse(configuration["PURVIEW_BLOCK_ENABLED"], out var block) && block;

        TokenCredential credential = new DefaultAzureCredential();

        // Register a named HttpClient for PurviewGraphClient. The factory manages connection
        // pooling and lifetime; PurviewGraphClient does not dispose the instance it receives.
        services.AddHttpClient("PurviewGraphClient");

        services.AddSingleton(baseSettings);
        services.AddSingleton(credential);
        services.AddSingleton<IPurviewAuditService>(sp =>
            new PurviewAuditService(
                sp.GetRequiredService<PurviewSettings>(),
                sp.GetRequiredService<TokenCredential>(),
                sp.GetRequiredService<ILogger<PurviewAuditService>>(),
                blockEnabled,
                sp.GetRequiredService<IHttpClientFactory>()));

        return services;
    }
}
