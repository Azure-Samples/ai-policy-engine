using System.Text.Json;
using AIPolicyEngine.Api.Models;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace AIPolicyEngine.Api.Services;

public sealed class ChargebackCalculator : IChargebackCalculator
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<ChargebackCalculator> _logger;
    private readonly object _cacheLock = new();

    // In-memory cache refreshed periodically to avoid hitting Redis on every calculation
    private Dictionary<string, ModelPricing> _pricingCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCacheRefresh = DateTime.MinValue;
    private volatile bool _refreshInProgress;
    private static readonly TimeSpan CacheRefreshInterval = TimeSpan.FromSeconds(30);
    private const int MaxCachedPricingEntries = 512;

    // Fallback defaults for when Redis has no pricing data
    private static readonly Dictionary<string, (decimal Prompt, decimal Completion, decimal Image)> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.2"] = (0.03m, 0.12m, 0m),
        ["gpt-5.3-codex"] = (0.035m, 0.14m, 0m),
        ["gpt-4.1"] = (0.02m, 0.08m, 0m),
        ["gpt-4.1-mini"] = (0.004m, 0.016m, 0m),
        ["gpt-4.1-nano"] = (0.001m, 0.004m, 0m),
        ["gpt-4o"] = (0.03m, 0.06m, 0m),
        ["gpt-4o-mini"] = (0.005m, 0.015m, 0m),
        ["gpt-4"] = (0.02m, 0.05m, 0m),
        ["gpt-oss-120b"] = (0.008m, 0.032m, 0m),
        ["text-embedding-3-large"] = (0.001m, 0.002m, 0m),
        ["dall-e-3"] = (0m, 0m, 0.009m),
    };

    public ChargebackCalculator(
        IConnectionMultiplexer redis,
        ILogger<ChargebackCalculator> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    // For testing without Redis
    public ChargebackCalculator() : this(null!, NullLogger<ChargebackCalculator>.Instance) { }

    /// <summary>For testing with a pre-seeded pricing cache (no Redis required).</summary>
    public ChargebackCalculator(Dictionary<string, ModelPricing> pricingCache)
        : this()
    {
        _pricingCache = new Dictionary<string, ModelPricing>(pricingCache, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RefreshCacheIfNeeded()
    {
        if (DateTime.UtcNow - _lastCacheRefresh < CacheRefreshInterval)
            return;

        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _lastCacheRefresh < CacheRefreshInterval)
                return;
            if (_refreshInProgress)
                return;
            _refreshInProgress = true;
        }

        if (_redis is null)
        {
            _refreshInProgress = false;
            return;
        }

        // Actual Redis read stays outside the lock (async-safe)
        try
        {
            var db = _redis.GetDatabase();
            var keys = _redis.KeysFromAllServers(RedisKeys.PricingPrefix);
            var cache = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                if (cache.Count >= MaxCachedPricingEntries)
                {
                    _logger.LogWarning("Pricing cache refresh hit max size limit ({MaxCachedPricingEntries})", MaxCachedPricingEntries);
                    break;
                }

                var val = await db.StringGetAsync(key);
                if (!val.HasValue) continue;
                var pricing = JsonSerializer.Deserialize<ModelPricing>((string)val!, JsonConfig.Default);
                if (pricing is not null)
                    cache[pricing.ModelId] = pricing;
            }

            if (cache.Count > 0)
                _pricingCache = cache;

            // Only mark refresh time AFTER successful read — a failed read should allow
            // the next caller to retry immediately instead of suppressing for the full interval.
            _lastCacheRefresh = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh pricing cache from Redis; continuing with existing cache");
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void TriggerBackgroundRefresh()
    {
        var refreshTask = RefreshCacheIfNeeded();
        if (refreshTask.IsCompleted)
            return;

        _ = refreshTask.ContinueWith(
            t => _logger.LogWarning(t.Exception, "Pricing cache refresh task failed unexpectedly"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public decimal CalculateCost(CachedLogData logData)
    {
        // Keep request path non-blocking while refreshing pricing cache opportunistically.
        TriggerBackgroundRefresh();

        var key = logData.DeploymentId;

        // Try Redis-backed pricing first
        if (_pricingCache.TryGetValue(key, out var pricing) ||
            (logData.Model is not null && _pricingCache.TryGetValue(logData.Model, out pricing)))
        {
            var promptCost = logData.PromptTokens / 1000m * pricing.PromptRatePer1K;
            var completionCost = logData.CompletionTokens / 1000m * pricing.CompletionRatePer1K;
            var imageCost = logData.ImageTokens / 1000m * pricing.ImageRatePer1K;
            return Math.Round(promptCost + completionCost + imageCost, 4);
        }

        // Fallback to hardcoded defaults
        if (Defaults.TryGetValue(key, out var def) ||
            (logData.Model is not null && Defaults.TryGetValue(logData.Model, out def)))
        {
            var promptCost = logData.PromptTokens / 1000m * def.Prompt;
            var completionCost = logData.CompletionTokens / 1000m * def.Completion;
            var imageCost = logData.ImageTokens / 1000m * def.Image;
            return Math.Round(promptCost + completionCost + imageCost, 4);
        }

        return 0m;
    }

    public decimal CalculateCustomerCost(CachedLogData logData, PlanData plan)
    {
        if (!logData.IsOverbilled || plan.CostPerMillionTokens <= 0)
            return 0m;
        
        // Only charge for overbilled tokens
        var overbilledTokens = logData.TotalTokens; // The individual request's tokens (all overbilled)
        return Math.Round(overbilledTokens / 1_000_000m * plan.CostPerMillionTokens, 4);
    }

    public decimal CalculateEffectiveRequestCost(CachedLogData logData)
    {
        TriggerBackgroundRefresh();

        var key = logData.DeploymentId;

        if (_pricingCache.TryGetValue(key, out var pricing) ||
            (logData.Model is not null && _pricingCache.TryGetValue(logData.Model, out pricing)))
        {
            // Invalid multipliers (zero or negative) default to 1.0
            return pricing.Multiplier > 0 ? pricing.Multiplier : 1.0m;
        }

        // Unknown deployment/model — default to 1.0
        return 1.0m;
    }

    public decimal CalculateMultiplierOverageCost(decimal effectiveCost, decimal currentUsage, PlanData plan)
    {
        if (!plan.UseMultiplierBilling) return 0m;
        if (plan.MonthlyRequestQuota == 0) return 0m; // unlimited

        var totalAfter = currentUsage + effectiveCost;
        if (totalAfter <= plan.MonthlyRequestQuota) return 0m;

        // Overage is capped at effectiveCost (can't overage more than the current request)
        var overageUnits = Math.Min(effectiveCost, Math.Max(0m, totalAfter - plan.MonthlyRequestQuota));
        return Math.Round(overageUnits * plan.OverageRatePerRequest, 4);
    }

    public string GetTierName(string deploymentId, string? model)
    {
        TriggerBackgroundRefresh();

        if (_pricingCache.TryGetValue(deploymentId, out var pricing) ||
            (model is not null && _pricingCache.TryGetValue(model, out pricing)))
        {
            return !string.IsNullOrEmpty(pricing.TierName) ? pricing.TierName : "Standard";
        }

        return "Standard";
    }

    public decimal GetMultiplier(string deploymentId, string? model)
    {
        TriggerBackgroundRefresh();

        if (_pricingCache.TryGetValue(deploymentId, out var pricing) ||
            (model is not null && _pricingCache.TryGetValue(model, out pricing)))
        {
            return pricing.Multiplier > 0 ? pricing.Multiplier : 1.0m;
        }

        return 1.0m;
    }
}
