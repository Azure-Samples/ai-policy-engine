using AIPolicyEngine.Api.Models;
using static AIPolicyEngine.Tests.Services.AccessProfiles.AccessProfileTestSupport;

namespace AIPolicyEngine.Tests.Services.AccessProfiles;

public sealed class AccessProfileResolverTests
{
    private const string ClientAppId = "client-123";
    private const string TenantId = "tenant-123";

    [Fact]
    public async Task ResolveAsync_OperationSpecificProfile_BeatsApiWideProfile()
    {
        using var harness = CreateResolverHarness(
        [
            CreateAccessProfile(ClientAppId, TenantId, "openai-api", null, "api-plan"),
            CreateAccessProfile(ClientAppId, TenantId, "openai-api", "chat", "operation-plan")
        ],
        legacyAssignment: CreateLegacyAssignment(planId: "legacy-plan"));

        var resolved = await harness.ResolveAsync(ClientAppId, TenantId, "openai-api", "chat");

        Assert.NotNull(resolved);
        Assert.Equal("operation-plan", resolved!.PlanId);
        Assert.Equal($"ap:{ClientAppId}:{TenantId}:openai-api:chat", resolved.SourceProfileId);
    }

    [Fact]
    public async Task ResolveAsync_ApiWideProfile_BeatsClientGlobalProfile()
    {
        using var harness = CreateResolverHarness(
        [
            CreateAccessProfile(ClientAppId, TenantId, "_global", null, "global-plan"),
            CreateAccessProfile(ClientAppId, TenantId, "openai-api", null, "api-plan")
        ],
        legacyAssignment: CreateLegacyAssignment(planId: "legacy-plan"));

        var resolved = await harness.ResolveAsync(ClientAppId, TenantId, "openai-api", "embeddings");

        Assert.NotNull(resolved);
        Assert.Equal("api-plan", resolved!.PlanId);
        Assert.Equal($"ap:{ClientAppId}:{TenantId}:openai-api:_all", resolved.SourceProfileId);
    }

    [Fact]
    public async Task ResolveAsync_ClientGlobalProfile_BeatsLegacyAssignment()
    {
        using var harness = CreateResolverHarness(
        [
            CreateAccessProfile(ClientAppId, TenantId, "_global", null, "global-plan", routingPolicyId: "profile-routing", allowedDeployments: ["gpt-4o"])
        ],
        legacyAssignment: CreateLegacyAssignment(planId: "legacy-plan", routingPolicyId: "legacy-routing", allowedDeployments: ["gpt-4o-mini"]));

        var resolved = await harness.ResolveAsync(ClientAppId, TenantId, "openai-api", null);

        Assert.NotNull(resolved);
        Assert.Equal("global-plan", resolved!.PlanId);
        Assert.Equal("profile-routing", resolved.RoutingPolicyId);
        Assert.Equal(["gpt-4o"], resolved.AllowedDeployments);
        Assert.Equal($"ap:{ClientAppId}:{TenantId}:_global:_all", resolved.SourceProfileId);
    }

    [Fact]
    public async Task ResolveAsync_NoProfiles_ReturnsLegacyAssignment()
    {
        using var harness = CreateResolverHarness([], CreateLegacyAssignment(planId: "legacy-plan", routingPolicyId: "legacy-routing", allowedDeployments: ["gpt-4o-mini"]));

        var resolved = await harness.ResolveAsync(ClientAppId, TenantId, "openai-api", "chat");

        Assert.NotNull(resolved);
        Assert.Equal("legacy-plan", resolved!.PlanId);
        Assert.Equal("legacy-routing", resolved.RoutingPolicyId);
        Assert.Equal(["gpt-4o-mini"], resolved.AllowedDeployments);
        Assert.Null(resolved.SourceProfileId);
    }

    [Fact]
    public async Task ResolveAsync_NoProfilesAndNoLegacyAssignment_ReturnsNull()
    {
        using var harness = CreateResolverHarness([], legacyAssignment: null);

        var resolved = await harness.ResolveAsync(ClientAppId, TenantId, "openai-api", "chat");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveAsync_DisabledProfile_IsSkippedDuringCascade()
    {
        using var harness = CreateResolverHarness(
        [
            CreateAccessProfile(ClientAppId, TenantId, "openai-api", "chat", "disabled-operation-plan", enabled: false),
            CreateAccessProfile(ClientAppId, TenantId, "openai-api", null, "api-plan")
        ],
        legacyAssignment: CreateLegacyAssignment(planId: "legacy-plan"));

        var resolved = await harness.ResolveAsync(ClientAppId, TenantId, "openai-api", "chat");

        Assert.NotNull(resolved);
        Assert.Equal("api-plan", resolved!.PlanId);
        Assert.Equal($"ap:{ClientAppId}:{TenantId}:openai-api:_all", resolved.SourceProfileId);
    }

    private static ClientPlanAssignment CreateLegacyAssignment(string planId, string? routingPolicyId = null, List<string>? allowedDeployments = null)
        => new()
        {
            Id = $"{ClientAppId}:{TenantId}",
            ClientAppId = ClientAppId,
            TenantId = TenantId,
            PlanId = planId,
            DisplayName = "Resolver Test Client",
            ModelRoutingPolicyOverride = routingPolicyId,
            AllowedDeployments = allowedDeployments ?? [],
            CurrentPeriodStart = new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc)
        };
    }
