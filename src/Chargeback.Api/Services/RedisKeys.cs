namespace Chargeback.Api.Services;

/// <summary>
/// Centralized Redis key patterns to eliminate magic strings across the codebase.
/// A "Customer" is identified by the clientAppId:tenantId combination throughout.
/// </summary>
public static class RedisKeys
{
    public static string Plan(string planId) => $"plan:{planId}";
    public static string Pricing(string modelId) => $"pricing:{modelId}";
    public static string UsagePolicySettings => "settings:usage-policy";

    /// <summary>
    /// Builds the combined customer key used as the atomic billing unit.
    /// </summary>
    public static string CustomerKey(string clientAppId, string tenantId) => $"{clientAppId}:{tenantId}";

    public static string Client(string clientAppId, string tenantId) => $"client:{clientAppId}:{tenantId}";
    public static string Traces(string clientAppId, string tenantId) => $"traces:{clientAppId}:{tenantId}";
    public static string ClientUpdateLock(string clientAppId, string tenantId) => $"lock:client:{clientAppId}:{tenantId}";
    public static string RateLimitRpm(string clientAppId, string tenantId, long minuteWindow) => $"ratelimit:rpm:{clientAppId}:{tenantId}:{minuteWindow}";
    public static string RateLimitTpm(string clientAppId, string tenantId, long minuteWindow) => $"ratelimit:tpm:{clientAppId}:{tenantId}:{minuteWindow}";
    public static string LogEntry(string clientAppId, string tenantId, string deploymentId) => $"log:{clientAppId}:{tenantId}:{deploymentId}";
    public static string CustomerLogPattern(string clientAppId, string tenantId) => $"log:{clientAppId}:{tenantId}:*";

    public static string RoutingPolicy(string policyId) => $"routing-policy:{policyId}";
    public const string RoutingPolicyPrefix = "routing-policy:*";

    // Deployment-scoped rate limit keys (rate limits apply to routed deployment)
    public static string RateLimitRpm(string clientAppId, string tenantId, string deploymentId, long minuteWindow)
        => $"ratelimit:rpm:{clientAppId}:{tenantId}:{deploymentId}:{minuteWindow}";
    public static string RateLimitTpm(string clientAppId, string tenantId, string deploymentId, long minuteWindow)
        => $"ratelimit:tpm:{clientAppId}:{tenantId}:{deploymentId}:{minuteWindow}";

    // Key prefix patterns for scanning
    public const string PlanPrefix = "plan:*";
    public const string ClientPrefix = "client:*";
    public const string TracesPrefix = "traces:*";
    public const string PricingPrefix = "pricing:*";
    public const string LogEntryPrefix = "log:*";

    /// <summary>
    /// Prefixes used by non-log-entry data types — used to filter them out when scanning all keys.
    /// </summary>
    public static readonly string[] ExcludedPrefixes = ["plan:", "client:", "traces:", "pricing:", "ratelimit:", "quota:", "lock:", "settings:", "routing-policy:"];
}
