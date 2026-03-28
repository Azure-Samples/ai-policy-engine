using System.Text.Json;
using Chargeback.Api.Models;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Chargeback.Api.Services;

public sealed class ChargebackCalculator : IChargebackCalculator
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<ChargebackCalculator> _logger;

    // In-memory cache refreshed periodically to avoid hitting Redis on every calculation
    private Dictionary<string, ModelPricing> _pricingCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCacheRefresh = DateTime.MinValue;
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

    private async Task RefreshCacheIfNeeded()
    {
        if (DateTime.UtcNow - _lastCacheRefresh < CacheRefreshInterval)
            return;

        if (_redis is null)
            return;

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

            _lastCacheRefresh = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh pricing cache from Redis; continuing with existing cache");
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
}
