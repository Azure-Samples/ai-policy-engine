using System.Text.Json;
using Chargeback.Api.Models;
using StackExchange.Redis;

namespace Chargeback.Api.Services;

public sealed class LogDataService : ILogDataService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IChargebackCalculator _calculator;
    private readonly ChargebackMetrics _metrics;
    private readonly IAuditStore _auditStore;
    private readonly IUsagePolicyStore _usagePolicyStore;

    public LogDataService(
        IConnectionMultiplexer redis,
        IChargebackCalculator calculator,
        ChargebackMetrics metrics,
        IAuditStore auditStore,
        IUsagePolicyStore usagePolicyStore)
    {
        _redis = redis;
        _calculator = calculator;
        _metrics = metrics;
        _auditStore = auditStore;
        _usagePolicyStore = usagePolicyStore;
    }

    /// <inheritdoc />
    public async Task<List<LogEntry>> GetBillingPeriodSummariesAsync(ILogger logger, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var policy = await _usagePolicyStore.GetAsync(ct);
        var periodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(DateTime.UtcNow, policy.BillingCycleStartDay);

        // Determine which YYYY-MM periods overlap the current billing cycle.
        // If the cycle starts on day 1 only the current month is needed; otherwise
        // the period spans two calendar months and we query both.
        var periods = new HashSet<string> { $"{periodStart:yyyy-MM}" };
        if (policy.BillingCycleStartDay > 1)
        {
            var nextMonth = periodStart.AddMonths(1);
            periods.Add($"{nextMonth:yyyy-MM}");
        }

        var allSummaries = new List<BillingSummaryDocument>();
        foreach (var period in periods)
        {
            var summaries = await _auditStore.GetBillingSummariesAsync(period, ct);
            allSummaries.AddRange(summaries);
        }

        logger.LogInformation(
            "Fetched {Count} billing summaries from Cosmos DB for period(s): {Periods}",
            allSummaries.Count, string.Join(", ", periods));

        return allSummaries.Select(doc => new LogEntry
        {
            TenantId = doc.TenantId,
            ClientAppId = doc.ClientAppId,
            Audience = doc.Audience,
            DeploymentId = doc.DeploymentId,
            Model = doc.Model,
            PromptTokens = doc.PromptTokens,
            CompletionTokens = doc.CompletionTokens,
            TotalTokens = doc.TotalTokens,
            ImageTokens = doc.ImageTokens,
            TotalCost = doc.CostToUs.ToString("F4"),
            CostToUs = doc.CostToUs.ToString("F4"),
            CostToCustomer = doc.CostToCustomer.ToString("F4"),
            IsOverbilled = doc.IsOverbilled,
        }).ToList();
    }

    public async Task<List<LogEntry>> GetAllLogsAsync(ILogger logger)
    {
        var db = _redis.GetDatabase();
        var keys = _redis.KeysFromAllServers(RedisKeys.LogEntryPrefix);

        logger.LogInformation("Fetched {KeyCount} log keys from Redis", keys.Length);

        var logs = new List<LogEntry>();

        foreach (var key in keys)
        {
            var keyStr = key.ToString();
            if (RedisKeys.ExcludedPrefixes.Any(p => keyStr.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var value = await db.StringGetAsync(key);
                if (!value.HasValue) continue;
                var cached = JsonSerializer.Deserialize<CachedLogData>((string)value!, JsonConfig.Default);
                if (cached is null) continue;

                var cost = _calculator.CalculateCost(cached);
                _metrics.RecordCost((double)cost, cached.TenantId, cached.ClientAppId, cached.Model ?? "unknown");

                logs.Add(new LogEntry
                {
                    TenantId = cached.TenantId,
                    ClientAppId = cached.ClientAppId,
                    Audience = cached.Audience,
                    DeploymentId = cached.DeploymentId,
                    Model = cached.Model,
                    ObjectType = cached.ObjectType,
                    PromptTokens = cached.PromptTokens,
                    CompletionTokens = cached.CompletionTokens,
                    TotalTokens = cached.TotalTokens,
                    ImageTokens = cached.ImageTokens,
                    TotalCost = cost.ToString("F4"),
                    CostToUs = cached.CostToUs.ToString("F4"),
                    CostToCustomer = cached.CostToCustomer.ToString("F4"),
                    IsOverbilled = cached.IsOverbilled,
                });
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Failed to deserialize log for key {Key}", key);
            }
        }

        return logs;
    }

    public async Task<List<RequestLogEntry>> GetRequestLogsAsync(ILogger logger)
    {
        var db = _redis.GetDatabase();

        // Build client displayName lookup from client:* keys
        var clientDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clientKeys = _redis.KeysFromAllServers(RedisKeys.ClientPrefix);
        foreach (var ck in clientKeys)
        {
            var val = await db.StringGetAsync(ck);
            if (!val.HasValue) continue;
            try
            {
                var assignment = JsonSerializer.Deserialize<ClientPlanAssignment>((string)val!, JsonConfig.Default);
                if (assignment is not null)
                {
                    var customerKey = RedisKeys.CustomerKey(assignment.ClientAppId, assignment.TenantId);
                    clientDisplayNames[customerKey] = assignment.DisplayName ?? $"{assignment.ClientAppId}/{assignment.TenantId}";
                }
            }
            catch (JsonException) { }
        }

        // Collect trace records from ALL traces:* Redis lists
        var traceKeys = _redis.KeysFromAllServers(RedisKeys.TracesPrefix);
        var entries = new List<RequestLogEntry>();

        foreach (var traceKey in traceKeys)
        {
            var keyStr = traceKey.ToString();
            // traces:{clientAppId}:{tenantId} — extract customer key after "traces:" prefix
            var customerKey = keyStr.Length > 7 ? keyStr[7..] : keyStr;
            // Split customerKey into clientAppId and tenantId
            var separatorIdx = customerKey.IndexOf(':');
            var clientAppId = separatorIdx > 0 ? customerKey[..separatorIdx] : customerKey;
            var tenantId = separatorIdx > 0 ? customerKey[(separatorIdx + 1)..] : string.Empty;

            var items = await db.ListRangeAsync(traceKey, 0, 199);
            foreach (var item in items)
            {
                try
                {
                    var trace = JsonSerializer.Deserialize<TraceRecord>((string)item!, JsonConfig.Default);
                    if (trace is null) continue;

                    entries.Add(new RequestLogEntry
                    {
                        Timestamp = trace.Timestamp,
                        ClientAppId = clientAppId,
                        ClientDisplayName = clientDisplayNames.GetValueOrDefault(customerKey, customerKey),
                        TenantId = tenantId,
                        DeploymentId = trace.DeploymentId,
                        Model = trace.Model,
                        PromptTokens = trace.PromptTokens,
                        CompletionTokens = trace.CompletionTokens,
                        TotalTokens = trace.TotalTokens,
                        CostToUs = trace.CostToUs,
                        CostToCustomer = trace.CostToCustomer,
                        IsOverbilled = trace.IsOverbilled,
                        StatusCode = trace.StatusCode
                    });
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Failed to deserialize trace from {Key}", traceKey);
                }
            }
        }

        entries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return entries;
    }
}
