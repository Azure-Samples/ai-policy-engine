using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

public sealed class UsagePolicyStore : IUsagePolicyStore
{
    private readonly IRepository<UsagePolicySettings> _repo;
    private readonly UsagePolicySettings _defaults;
    private readonly ILogger<UsagePolicyStore> _logger;

    public UsagePolicyStore(
        IRepository<UsagePolicySettings> repo,
        IConfiguration configuration,
        ILogger<UsagePolicyStore> logger)
    {
        _repo = repo;
        _logger = logger;
        _defaults = Normalize(
            configuration.GetSection("UsagePolicy").Get<UsagePolicySettings>()
            ?? new UsagePolicySettings());
    }

    public async Task<UsagePolicySettings> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var settings = await _repo.GetAsync("usage-policy", ct);
            return settings is not null ? Normalize(settings) : Clone(_defaults);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read usage policy settings; falling back to defaults");
            return Clone(_defaults);
        }
    }

    public async Task<UsagePolicySettings> UpdateAsync(UsagePolicyUpdateRequest request, CancellationToken ct = default)
    {
        var current = await GetAsync(ct);

        if (request.BillingCycleStartDay.HasValue)
            current.BillingCycleStartDay = request.BillingCycleStartDay.Value;
        if (request.AggregatedLogRetentionDays.HasValue)
            current.AggregatedLogRetentionDays = request.AggregatedLogRetentionDays.Value;
        if (request.TraceRetentionDays.HasValue)
            current.TraceRetentionDays = request.TraceRetentionDays.Value;

        var normalized = Normalize(current);
        await _repo.UpsertAsync(normalized, ct);

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
