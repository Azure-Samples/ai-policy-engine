using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Custom OpenTelemetry metrics for chargeback tracking.
/// Emits counters and histograms to Azure Monitor / App Insights.
/// </summary>
public sealed class ChargebackMetrics
{
    public const string MeterName = "AIPolicyEngine.Api";

    private readonly Counter<long> _tokensProcessed;
    private readonly Counter<long> _requestsProcessed;
    private readonly Histogram<double> _costTotal;

    public ChargebackMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _tokensProcessed = meter.CreateCounter<long>(
            "aipolicy.tokens_processed",
            unit: "tokens",
            description: "Total tokens processed for AI policy tracking");

        _requestsProcessed = meter.CreateCounter<long>(
            "aipolicy.requests_processed",
            unit: "requests",
            description: "Total AI policy log requests processed");

        _costTotal = meter.CreateHistogram<double>(
            "aipolicy.cost_total",
            unit: "USD",
            description: "Cost distribution per AI policy entry");
    }

    public void RecordTokensProcessed(long tokens, string tenantId, string clientAppId, string model, string deploymentId)
    {
        _tokensProcessed.Add(tokens,
            new KeyValuePair<string, object?>("tenant_id", tenantId),
            new KeyValuePair<string, object?>("client_app_id", clientAppId),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("deployment_id", deploymentId));
    }

    public void RecordRequest(string tenantId, string clientAppId, string model)
    {
        _requestsProcessed.Add(1,
            new KeyValuePair<string, object?>("tenant_id", tenantId),
            new KeyValuePair<string, object?>("client_app_id", clientAppId),
            new KeyValuePair<string, object?>("model", model));
    }

    public void RecordCost(double cost, string tenantId, string clientAppId, string model)
    {
        _costTotal.Record(cost,
            new KeyValuePair<string, object?>("tenant_id", tenantId),
            new KeyValuePair<string, object?>("client_app_id", clientAppId),
            new KeyValuePair<string, object?>("model", model));
    }
}
