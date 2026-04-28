using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;

namespace AIPolicyEngine.Tests.Integration;

/// <summary>
/// B5.7 — Integration tests: Precheck with routing policies.
/// Tests the full precheck flow with routing evaluation, validating that
/// routing decisions correctly interact with AllowedDeployments, rate limits,
/// and default behaviors. Exercises the join point between routing and enforcement.
/// </summary>
public class PrecheckRoutingIntegrationTests
{
    // ─── Constants ──────────────────────────────────────────────────────

    private const string ClientAppId = "routing-client";
    private const string TenantId = "tenant-1";
    private const string PlanId = "plan-routing";

    // ─── Helpers ────────────────────────────────────────────────────────

    private static PlanData CreatePlan(
        string? routingPolicyId = null,
        long monthlyTokenQuota = 10_000_000,
        int requestsPerMinuteLimit = 0,
        int tokensPerMinuteLimit = 0,
        bool allowOverbilling = true,
        List<string>? allowedDeployments = null)
    {
        return new PlanData
        {
            Id = PlanId,
            Name = "Routing Test Plan",
            MonthlyRate = 99m,
            MonthlyTokenQuota = monthlyTokenQuota,
            RequestsPerMinuteLimit = requestsPerMinuteLimit,
            TokensPerMinuteLimit = tokensPerMinuteLimit,
            AllowOverbilling = allowOverbilling,
            CostPerMillionTokens = 5m,
            RollUpAllDeployments = true,
            ModelRoutingPolicyId = routingPolicyId,
            AllowedDeployments = allowedDeployments ?? [],
        };
    }

    private static ClientPlanAssignment CreateClient(
        string? routingPolicyOverride = null,
        List<string>? allowedDeployments = null)
    {
        return new ClientPlanAssignment
        {
            Id = $"{ClientAppId}:{TenantId}",
            ClientAppId = ClientAppId,
            TenantId = TenantId,
            PlanId = PlanId,
            DisplayName = "Routing Test Client",
            CurrentPeriodStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            CurrentPeriodUsage = 0,
            ModelRoutingPolicyOverride = routingPolicyOverride,
            AllowedDeployments = allowedDeployments ?? [],
        };
    }

    private static ModelRoutingPolicy CreatePolicy(
        string id,
        List<RouteRule> rules,
        RoutingBehavior defaultBehavior = RoutingBehavior.Passthrough,
        string? fallbackDeployment = null)
    {
        return new ModelRoutingPolicy
        {
            Id = id,
            Name = $"Policy {id}",
            Rules = rules,
            DefaultBehavior = defaultBehavior,
            FallbackDeployment = fallbackDeployment,
        };
    }

    /// <summary>
    /// Simulates the precheck routing + enforcement flow that Freamon
    /// will integrate into PrecheckEndpoints. Composes:
    ///   1. Resolve the effective routing policy (client override vs plan)
    ///   2. Evaluate routing decision
    ///   3. Validate AllowedDeployments against ROUTED deployment
    ///   4. Return routing result
    /// </summary>
    private static (RoutingResult routing, bool isAllowed, string? denyReason) SimulateRoutedPrecheck(
        string requestedDeployment,
        PlanData plan,
        ClientPlanAssignment client,
        ModelRoutingPolicy? planRoutingPolicy,
        ModelRoutingPolicy? clientOverridePolicy)
    {
        // Step 1: Resolve effective routing policy (client override takes precedence)
        var effectivePolicy = client.ModelRoutingPolicyOverride is not null
            ? clientOverridePolicy
            : planRoutingPolicy;

        // Step 2: Evaluate routing
        var routingResult = RoutingEvaluator.Evaluate(requestedDeployment, effectivePolicy);

        if (!routingResult.IsAllowed)
            return (routingResult, false, "Routing policy denied — no matching rule");

        var effectiveDeployment = routingResult.DeploymentId;

        // Step 3: Validate AllowedDeployments against ROUTED deployment
        var effectiveAllowedDeployments = (client.AllowedDeployments is { Count: > 0 })
            ? client.AllowedDeployments
            : plan.AllowedDeployments;

        if (effectiveAllowedDeployments is { Count: > 0 } &&
            !effectiveAllowedDeployments.Contains(effectiveDeployment, StringComparer.OrdinalIgnoreCase))
        {
            return (routingResult, false, $"Deployment '{effectiveDeployment}' not in allowed list");
        }

        return (routingResult, true, null);
    }

    // ─── B5.7.1: No routing policy — precheck works as before ──────────

    [Fact]
    public void NoRoutingPolicy_PrecheckWorksAsPassthrough()
    {
        var plan = CreatePlan(routingPolicyId: null);
        var client = CreateClient();

        var (routing, isAllowed, denyReason) = SimulateRoutedPrecheck(
            requestedDeployment: "gpt-4o",
            plan, client,
            planRoutingPolicy: null,
            clientOverridePolicy: null);

        Assert.True(isAllowed);
        Assert.Null(denyReason);
        Assert.False(routing.WasRouted);
        Assert.Equal("gpt-4o", routing.DeploymentId);
    }

    // ─── B5.7.2: Routing policy with matching rule ─────────────────────

    [Fact]
    public void RoutingPolicy_MatchingRule_ReturnsRoutedDeployment()
    {
        var policy = CreatePolicy("policy-1", [
            new RouteRule { RequestedDeployment = "gpt-4o", RoutedDeployment = "gpt-4o-eastus2", Priority = 1, Enabled = true }
        ]);
        var plan = CreatePlan(routingPolicyId: "policy-1", allowedDeployments: ["gpt-4o-eastus2"]);
        var client = CreateClient();

        var (routing, isAllowed, _) = SimulateRoutedPrecheck(
            "gpt-4o", plan, client, planRoutingPolicy: policy, clientOverridePolicy: null);

        Assert.True(isAllowed);
        Assert.True(routing.WasRouted);
        Assert.Equal("gpt-4o-eastus2", routing.DeploymentId);
    }

    // ─── B5.7.3: No match + Passthrough — original deployment ──────────

    [Fact]
    public void RoutingPolicy_NoMatchPassthrough_UsesOriginalDeployment()
    {
        var policy = CreatePolicy("policy-2", [
            new RouteRule { RequestedDeployment = "gpt-4", RoutedDeployment = "gpt-4-prod", Priority = 1, Enabled = true }
        ], RoutingBehavior.Passthrough);
        var plan = CreatePlan(routingPolicyId: "policy-2");
        var client = CreateClient();

        var (routing, isAllowed, _) = SimulateRoutedPrecheck(
            "gpt-4o", plan, client, planRoutingPolicy: policy, clientOverridePolicy: null);

        Assert.True(isAllowed);
        Assert.False(routing.WasRouted);
        Assert.Equal("gpt-4o", routing.DeploymentId);
    }

    // ─── B5.7.4: No match + Deny — request blocked ────────────────────

    [Fact]
    public void RoutingPolicy_NoMatchDeny_ReturnsDenied()
    {
        var policy = CreatePolicy("policy-deny", [
            new RouteRule { RequestedDeployment = "gpt-4", RoutedDeployment = "gpt-4-prod", Priority = 1, Enabled = true }
        ], RoutingBehavior.Deny);
        var plan = CreatePlan(routingPolicyId: "policy-deny");
        var client = CreateClient();

        var (routing, isAllowed, denyReason) = SimulateRoutedPrecheck(
            "gpt-4o", plan, client, planRoutingPolicy: policy, clientOverridePolicy: null);

        Assert.False(isAllowed);
        Assert.NotNull(denyReason);
        Assert.Contains("denied", denyReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(routing.IsAllowed);
    }

    // ─── B5.7.5: Client override takes precedence over plan ────────────

    [Fact]
    public void ClientRoutingOverride_TakesPrecedenceOverPlan()
    {
        var planPolicy = CreatePolicy("plan-policy", [
            new RouteRule { RequestedDeployment = "gpt-4o", RoutedDeployment = "gpt-4o-plan-route", Priority = 1, Enabled = true }
        ]);
        var clientPolicy = CreatePolicy("client-policy", [
            new RouteRule { RequestedDeployment = "gpt-4o", RoutedDeployment = "gpt-4o-client-route", Priority = 1, Enabled = true }
        ]);
        var plan = CreatePlan(routingPolicyId: "plan-policy");
        var client = CreateClient(routingPolicyOverride: "client-policy");

        var (routing, isAllowed, _) = SimulateRoutedPrecheck(
            "gpt-4o", plan, client, planRoutingPolicy: planPolicy, clientOverridePolicy: clientPolicy);

        Assert.True(isAllowed);
        Assert.True(routing.WasRouted);
        // Client override wins: routes to client-route, NOT plan-route
        Assert.Equal("gpt-4o-client-route", routing.DeploymentId);
    }

    // ─── B5.7.6: AllowedDeployments validated against ROUTED deployment ─

    [Theory]
    [InlineData("gpt-4o-eastus2", true)]   // routed deployment is allowed
    [InlineData("gpt-4o-westus", false)]    // routed deployment is NOT allowed
    public void AllowedDeployments_ValidatedAgainstRoutedDeployment(string routedTo, bool expectAllowed)
    {
        var policy = CreatePolicy("policy-acl", [
            new RouteRule { RequestedDeployment = "gpt-4o", RoutedDeployment = routedTo, Priority = 1, Enabled = true }
        ]);
        // AllowedDeployments contains eastus2 but NOT westus — validates ROUTED, not requested
        var plan = CreatePlan(routingPolicyId: "policy-acl", allowedDeployments: ["gpt-4o-eastus2"]);
        var client = CreateClient();

        var (_, isAllowed, denyReason) = SimulateRoutedPrecheck(
            "gpt-4o", plan, client, planRoutingPolicy: policy, clientOverridePolicy: null);

        Assert.Equal(expectAllowed, isAllowed);
        if (!expectAllowed)
            Assert.Contains("not in allowed list", denyReason!);
    }

    // ─── B5.7.7: Rate limits checked against ROUTED deployment ─────────

    [Fact]
    public void RateLimits_KeysUseRoutedDeployment_NotRequested()
    {
        var requestedDeployment = "gpt-4o";
        var routedDeployment = "gpt-4o-eastus2";

        // Verify rate limit key generation uses the routed deployment
        var rpmKeyRequested = RedisKeys.RateLimitRpm(ClientAppId, TenantId, requestedDeployment, 123456);
        var rpmKeyRouted = RedisKeys.RateLimitRpm(ClientAppId, TenantId, routedDeployment, 123456);
        var tpmKeyRequested = RedisKeys.RateLimitTpm(ClientAppId, TenantId, requestedDeployment, 123456);
        var tpmKeyRouted = RedisKeys.RateLimitTpm(ClientAppId, TenantId, routedDeployment, 123456);

        // Rate limit keys must differ — they're per-deployment
        Assert.NotEqual(rpmKeyRequested, rpmKeyRouted);
        Assert.NotEqual(tpmKeyRequested, tpmKeyRouted);

        // Routed deployment keys must contain the routed deployment ID
        Assert.Contains(routedDeployment, rpmKeyRouted);
        Assert.Contains(routedDeployment, tpmKeyRouted);
    }

    [Fact]
    public async Task RateLimits_RoutedDeployment_EnforcedAgainstRouted()
    {
        var redis = new FakeRedis();

        // Simulate: request routed from gpt-4o → gpt-4o-eastus2
        // RPM limit = 5, already at 5 on routed deployment
        var routedDeployment = "gpt-4o-eastus2";
        var minuteWindow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var rpmKey = RedisKeys.RateLimitRpm(ClientAppId, TenantId, routedDeployment, minuteWindow);
        redis.SeedString(rpmKey, "5");

        var plan = CreatePlan(requestsPerMinuteLimit: 5);
        var db = redis.Database;

        // Check RPM on ROUTED deployment — should be at limit
        var currentRpm = (long)(await db.StringGetAsync(rpmKey));
        Assert.Equal(5, currentRpm);
        Assert.True(currentRpm >= plan.RequestsPerMinuteLimit);

        // The REQUESTED deployment should have no rate limit state
        var requestedRpmKey = RedisKeys.RateLimitRpm(ClientAppId, TenantId, "gpt-4o", minuteWindow);
        var requestedRpm = await db.StringGetAsync(requestedRpmKey);
        Assert.True(requestedRpm.IsNull);
    }

    // ─── B5.7.8: Disabled rule is skipped ──────────────────────────────

    [Fact]
    public void DisabledRoutingRule_IsSkippedInPrecheckFlow()
    {
        var policy = CreatePolicy("policy-disabled", [
            new RouteRule { RequestedDeployment = "gpt-4o", RoutedDeployment = "gpt-4o-disabled-target", Priority = 1, Enabled = false },
            new RouteRule { RequestedDeployment = "gpt-4o", RoutedDeployment = "gpt-4o-enabled-target", Priority = 2, Enabled = true }
        ]);
        var plan = CreatePlan(routingPolicyId: "policy-disabled", allowedDeployments: ["gpt-4o-enabled-target"]);
        var client = CreateClient();

        var (routing, isAllowed, _) = SimulateRoutedPrecheck(
            "gpt-4o", plan, client, planRoutingPolicy: policy, clientOverridePolicy: null);

        Assert.True(isAllowed);
        Assert.True(routing.WasRouted);
        // Disabled rule skipped; enabled rule selected
        Assert.Equal("gpt-4o-enabled-target", routing.DeploymentId);
    }

    // ─── B5.7.9: FallbackDeployment used when no rules match ───────────

    [Fact]
    public void FallbackDeployment_UsedWhenNoRulesMatchInPrecheckFlow()
    {
        var policy = CreatePolicy("policy-fallback",
            rules: [
                new RouteRule { RequestedDeployment = "gpt-4", RoutedDeployment = "gpt-4-prod", Priority = 1, Enabled = true }
            ],
            defaultBehavior: RoutingBehavior.Passthrough,
            fallbackDeployment: "gpt-4o-fallback-region");
        var plan = CreatePlan(routingPolicyId: "policy-fallback",
            allowedDeployments: ["gpt-4o-fallback-region", "gpt-4-prod"]);
        var client = CreateClient();

        // Request for gpt-4o — no matching rule, fallback used
        var (routing, isAllowed, _) = SimulateRoutedPrecheck(
            "gpt-4o", plan, client, planRoutingPolicy: policy, clientOverridePolicy: null);

        Assert.True(isAllowed);
        Assert.True(routing.WasRouted);
        Assert.Equal("gpt-4o-fallback-region", routing.DeploymentId);
    }

    // ─── B5.7 Edge: Client override + Deny — client-scoped denial ──────

    [Fact]
    public void ClientOverrideDenyPolicy_BlocksEvenWhenPlanAllows()
    {
        var planPolicy = CreatePolicy("plan-allow", [
            new RouteRule { RequestedDeployment = "gpt-4o", RoutedDeployment = "gpt-4o-prod", Priority = 1, Enabled = true }
        ], RoutingBehavior.Passthrough);

        // Client override: Deny everything except gpt-4
        var clientPolicy = CreatePolicy("client-deny", [
            new RouteRule { RequestedDeployment = "gpt-4", RoutedDeployment = "gpt-4-limited", Priority = 1, Enabled = true }
        ], RoutingBehavior.Deny);

        var plan = CreatePlan(routingPolicyId: "plan-allow");
        var client = CreateClient(routingPolicyOverride: "client-deny");

        // gpt-4o: client policy has no rule for it + Deny → blocked
        var (_, isAllowed, _) = SimulateRoutedPrecheck(
            "gpt-4o", plan, client, planRoutingPolicy: planPolicy, clientOverridePolicy: clientPolicy);
        Assert.False(isAllowed);

        // gpt-4: client policy has a rule → allowed
        var (routing2, isAllowed2, _) = SimulateRoutedPrecheck(
            "gpt-4", plan, client, planRoutingPolicy: planPolicy, clientOverridePolicy: clientPolicy);
        Assert.True(isAllowed2);
        Assert.Equal("gpt-4-limited", routing2.DeploymentId);
    }

    // ─── B5.7 Edge: Routing + AllowedDeployments empty = all allowed ───

    [Fact]
    public void RoutingWithEmptyAllowedDeployments_AllowsAnyRoutedDeployment()
    {
        var policy = CreatePolicy("policy-open", [
            new RouteRule { RequestedDeployment = "gpt-4o", RoutedDeployment = "gpt-4o-anywhere", Priority = 1, Enabled = true }
        ]);
        var plan = CreatePlan(routingPolicyId: "policy-open", allowedDeployments: []);
        var client = CreateClient();

        var (routing, isAllowed, _) = SimulateRoutedPrecheck(
            "gpt-4o", plan, client, planRoutingPolicy: policy, clientOverridePolicy: null);

        Assert.True(isAllowed);
        Assert.True(routing.WasRouted);
        Assert.Equal("gpt-4o-anywhere", routing.DeploymentId);
    }
}
