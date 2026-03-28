using System.Text.Json;
using Chargeback.Api.Models;
using StackExchange.Redis;

namespace Chargeback.Api.Services;

public sealed class UsagePolicyStore : IUsagePolicyStore
{
    private readonly UsagePolicySettings _defaults;
    private readonly ILogger<UsagePolicyStore> _logger;

    public UsagePolicyStore(IConfiguration configuration, ILogger<UsagePolicyStore> logger)
    {
        _logger = logger;
        _defaults = Normalize(
            configuration.GetSection("UsagePolicy").Get<UsagePolicySettings>()
            ?? new UsagePolicySettings());
    }

    public async Task<UsagePolicySettings> GetAsync(IDatabase db)
    {
        var value = await db.StringGetAsync(RedisKeys.UsagePolicySettings);
        if (!value.HasValue)
            return Clone(_defaults);

        try
        {
            var settings = JsonSerializer.Deserialize<UsagePolicySettings>((string)value!, JsonConfig.Default);
            return settings is null ? Clone(_defaults) : Normalize(settings);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize usage policy settings; falling back to defaults");
            return Clone(_defaults);
        }
    }

    public async Task<UsagePolicySettings> UpdateAsync(IDatabase db, UsagePolicyUpdateRequest request)
    {
        var current = await GetAsync(db);

        if (request.BillingCycleStartDay.HasValue)
            current.BillingCycleStartDay = request.BillingCycleStartDay.Value;
        if (request.AggregatedLogRetentionDays.HasValue)
            current.AggregatedLogRetentionDays = request.AggregatedLogRetentionDays.Value;
        if (request.TraceRetentionDays.HasValue)
            current.TraceRetentionDays = request.TraceRetentionDays.Value;

        var normalized = Normalize(current);
        var payload = JsonSerializer.Serialize(normalized, JsonConfig.Default);
        await db.StringSetAsync(RedisKeys.UsagePolicySettings, payload);

        return normalized;
    }

    private static UsagePolicySettings Normalize(UsagePolicySettings settings)
    {
        settings.BillingCycleStartDay = BillingPeriodCalculator.NormalizeCycleStartDay(settings.BillingCycleStartDay);
        settings.AggregatedLogRetentionDays = Math.Clamp(settings.AggregatedLogRetentionDays, 1, 365);
        settings.TraceRetentionDays = Math.Clamp(settings.TraceRetentionDays, 1, 365);
        return settings;
    }

    private static UsagePolicySettings Clone(UsagePolicySettings settings) => new()
    {
        BillingCycleStartDay = settings.BillingCycleStartDay,
        AggregatedLogRetentionDays = settings.AggregatedLogRetentionDays,
        TraceRetentionDays = settings.TraceRetentionDays
    };
}
