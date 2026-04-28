namespace AIPolicyEngine.Api.Models;

/// <summary>
/// A single routing rule that maps a requested deployment to a routed deployment.
/// Uses exact Foundry deployment matches — no glob or regex.
/// </summary>
public sealed class RouteRule
{
    /// <summary>The deployment the client originally requested (exact match).</summary>
    public string RequestedDeployment { get; set; } = string.Empty;

    /// <summary>The deployment the request is actually routed to (exact match).</summary>
    public string RoutedDeployment { get; set; } = string.Empty;

    /// <summary>Lower number = higher priority. Used to resolve conflicts when multiple rules match.</summary>
    public int Priority { get; set; }

    /// <summary>Whether this rule is active. Disabled rules are skipped during evaluation.</summary>
    public bool Enabled { get; set; } = true;
}
