using Azure.Core;

namespace Chargeback.Api.Services;

/// <summary>
/// Extension methods for configuring Agent365 Observability SDK integration.
/// Uses OpenTelemetry with optional A365 exporter (controlled by env var).
/// </summary>
/// <remarks>
/// NOTE: This is a minimal stub implementation for SDK version 0.1.75-beta.
/// Once the SDK stabilizes with documented AddA365Tracing APIs, this should be
/// updated to properly configure the exporter and token resolver.
/// </remarks>
public static class Agent365ServiceExtensions
{
    /// <summary>
    /// Adds Agent365 Observability SDK with OTel integration.
    /// Exporter is opt-in via ENABLE_A365_OBSERVABILITY_EXPORTER env var.
    /// </summary>
    public static IHostApplicationBuilder AddAgent365Observability(
        this IHostApplicationBuilder builder)
    {
        var enabled = bool.TryParse(
            builder.Configuration["ENABLE_A365_OBSERVABILITY_EXPORTER"],
            out var value) && value;

        if (!enabled)
        {
            // A365 not configured — register no-op service
            builder.Services.AddSingleton<IAgent365ObservabilityService, NoOpAgent365ObservabilityService>();
            return builder;
        }

        // TODO: Once SDK exports AddA365Tracing extension, configure here
        // builder.AddA365Tracing(
        //     configure: null,
        //     useOpenTelemetryBuilder: true,
        //     agent365ExporterType: Agent365ExporterType.Agent365ExporterAsync);

        // Register observability service with real implementation
        builder.Services.AddSingleton<IAgent365ObservabilityService, Agent365ObservabilityService>();

        return builder;
    }
}
