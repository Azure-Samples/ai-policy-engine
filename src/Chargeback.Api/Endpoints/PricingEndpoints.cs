using System.Text.Json;
using Chargeback.Api.Models;
using Chargeback.Api.Services;
using StackExchange.Redis;

namespace Chargeback.Api.Endpoints;

/// <summary>
/// CRUD endpoints for model pricing configuration stored in Redis.
/// </summary>
public static class PricingEndpoints
{
    private static readonly Dictionary<string, ModelPricing> DefaultPricing = new()
    {
        ["gpt-5.2"] = new() { ModelId = "gpt-5.2", DisplayName = "GPT-5.2", PromptRatePer1K = 0.03m, CompletionRatePer1K = 0.12m },
        ["gpt-5.3-codex"] = new() { ModelId = "gpt-5.3-codex", DisplayName = "GPT-5.3 Codex", PromptRatePer1K = 0.035m, CompletionRatePer1K = 0.14m },
        ["gpt-4.1"] = new() { ModelId = "gpt-4.1", DisplayName = "GPT-4.1", PromptRatePer1K = 0.02m, CompletionRatePer1K = 0.08m },
        ["gpt-4.1-mini"] = new() { ModelId = "gpt-4.1-mini", DisplayName = "GPT-4.1 Mini", PromptRatePer1K = 0.004m, CompletionRatePer1K = 0.016m },
        ["gpt-4.1-nano"] = new() { ModelId = "gpt-4.1-nano", DisplayName = "GPT-4.1 Nano", PromptRatePer1K = 0.001m, CompletionRatePer1K = 0.004m },
        ["gpt-4o"] = new() { ModelId = "gpt-4o", DisplayName = "GPT-4o", PromptRatePer1K = 0.03m, CompletionRatePer1K = 0.06m },
        ["gpt-4o-mini"] = new() { ModelId = "gpt-4o-mini", DisplayName = "GPT-4o Mini", PromptRatePer1K = 0.005m, CompletionRatePer1K = 0.015m },
        ["gpt-4"] = new() { ModelId = "gpt-4", DisplayName = "GPT-4", PromptRatePer1K = 0.02m, CompletionRatePer1K = 0.05m },
        ["gpt-oss-120b"] = new() { ModelId = "gpt-oss-120b", DisplayName = "GPT-OSS 120B", PromptRatePer1K = 0.008m, CompletionRatePer1K = 0.032m },
        ["text-embedding-3-large"] = new() { ModelId = "text-embedding-3-large", DisplayName = "Text Embedding 3 Large", PromptRatePer1K = 0.001m, CompletionRatePer1K = 0.002m },
        ["dall-e-3"] = new() { ModelId = "dall-e-3", DisplayName = "DALL-E 3", ImageRatePer1K = 0.009m },
    };

    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/pricing", GetPricing)
            .WithName("GetPricing")
            .WithDescription("List all model pricing configurations")
            .RequireAuthorization()
            .Produces<ModelPricingResponse>();

        routes.MapPut("/api/pricing/{modelId}", UpsertPricing)
            .WithName("UpsertPricing")
            .WithDescription("Create or update pricing for a model")
            .RequireAuthorization("AdminPolicy")
            .Produces<ModelPricing>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapDelete("/api/pricing/{modelId}", DeletePricing)
            .WithName("DeletePricing")
            .WithDescription("Delete pricing configuration for a model")
            .RequireAuthorization("AdminPolicy")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> GetPricing(
        IConnectionMultiplexer redis,
        ILogger<ModelPricingResponse> logger)
    {
        try
        {
            var db = redis.GetDatabase();
            var keys = redis.KeysFromAllServers(RedisKeys.PricingPrefix);

            // Seed defaults on first run
            if (keys.Length == 0)
            {
                logger.LogInformation("No pricing keys found — seeding defaults");
                foreach (var (modelId, pricing) in DefaultPricing)
                {
                    pricing.UpdatedAt = DateTime.UtcNow;
                    var cacheKey = RedisKeys.Pricing(modelId);
                    var cacheValue = JsonSerializer.Serialize(pricing, JsonConfig.Default);
                    await db.StringSetAsync(cacheKey, cacheValue);
                }

                keys = redis.KeysFromAllServers(RedisKeys.PricingPrefix);
            }

            logger.LogInformation("Fetched {KeyCount} pricing keys from Redis", keys.Length);

            var models = new List<ModelPricing>();
            foreach (var key in keys)
            {
                var value = await db.StringGetAsync(key);
                if (!value.HasValue) continue;

                try
                {
                    var pricing = JsonSerializer.Deserialize<ModelPricing>((string)value!, JsonConfig.Default);
                    if (pricing is not null)
                        models.Add(pricing);
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Failed to deserialize pricing for key {Key}", key);
                }
            }

            return Results.Json(new ModelPricingResponse { Models = models }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching pricing");
            return Results.Json(new { error = "Failed to fetch pricing" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> UpsertPricing(
        string modelId,
        ModelPricingCreateRequest body,
        IConnectionMultiplexer redis,
        ILogger<ModelPricing> logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return Results.BadRequest("modelId is required");

            var pricing = new ModelPricing
            {
                ModelId = modelId,
                DisplayName = body.DisplayName ?? modelId,
                PromptRatePer1K = body.PromptRatePer1K,
                CompletionRatePer1K = body.CompletionRatePer1K,
                ImageRatePer1K = body.ImageRatePer1K,
                UpdatedAt = DateTime.UtcNow
            };

            var db = redis.GetDatabase();
            var cacheKey = RedisKeys.Pricing(modelId);
            var cacheValue = JsonSerializer.Serialize(pricing, JsonConfig.Default);
            await db.StringSetAsync(cacheKey, cacheValue);

            logger.LogInformation("Pricing upserted: ModelId={ModelId}", modelId);
            return Results.Json(pricing, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error upserting pricing for {ModelId}", modelId);
            return Results.Json(new { error = "Failed to upsert pricing" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DeletePricing(
        string modelId,
        IConnectionMultiplexer redis,
        ILogger<ModelPricing> logger)
    {
        try
        {
            var db = redis.GetDatabase();
            var cacheKey = RedisKeys.Pricing(modelId);
            var deleted = await db.KeyDeleteAsync(cacheKey);

            if (!deleted)
                return Results.NotFound(new { error = $"Pricing for model '{modelId}' not found" });

            logger.LogInformation("Pricing deleted: ModelId={ModelId}", modelId);
            return Results.Ok(new { message = "Pricing deleted successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting pricing for {ModelId}", modelId);
            return Results.Json(new { error = "Failed to delete pricing" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
