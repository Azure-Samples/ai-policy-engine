namespace AIPolicyEngine.Api.Services;

public sealed class FoundryDiscoveryOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public bool DiscoverAllSubscriptions { get; set; }
    public List<string> SubscriptionIds { get; set; } = [];
}
