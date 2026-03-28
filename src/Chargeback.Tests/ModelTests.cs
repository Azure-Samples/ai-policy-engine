using Chargeback.Api.Models;
using Chargeback.Api.Services;

namespace Chargeback.Tests;

public class ModelTests
{
    [Fact]
    public void LogIngestRequest_DefaultValues()
    {
        var request = new LogIngestRequest();

        Assert.Equal(string.Empty, request.TenantId);
        Assert.Equal(string.Empty, request.ClientAppId);
        Assert.Equal(string.Empty, request.Audience);
        Assert.Equal(string.Empty, request.DeploymentId);
        Assert.Null(request.RequestBody);
        Assert.Null(request.ResponseBody);
    }

    [Fact]
    public void CachedLogData_IncrementalAggregation()
    {
        var cached = new CachedLogData
        {
            TenantId = "tenant-1",
            ClientAppId = "app-1",
            DeploymentId = "gpt-4o",
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150
        };

        // Simulate incremental update
        cached.PromptTokens += 200;
        cached.CompletionTokens += 100;
        cached.TotalTokens += 300;

        Assert.Equal(300, cached.PromptTokens);
        Assert.Equal(150, cached.CompletionTokens);
        Assert.Equal(450, cached.TotalTokens);
    }

    [Fact]
    public void LogEntry_HasExpectedDefaults()
    {
        var entry = new LogEntry();

        Assert.Equal(string.Empty, entry.TenantId);
        Assert.Equal("0.00", entry.TotalCost);
        Assert.Equal(0, entry.PromptTokens);
    }

    [Fact]
    public void ChargebackResponse_HasExpectedDefaults()
    {
        var response = new ChargebackResponse();

        Assert.Equal("0.00", response.TotalChargeback);
        Assert.NotNull(response.Logs);
        Assert.Empty(response.Logs);
    }

    [Fact]
    public void RedisKey_Format()
    {
        var tenantId = "00000000-0000-0000-0000-000000000001";
        var clientAppId = "app-12345";
        var deploymentId = "gpt-4o";

        var key = RedisKeys.LogEntry(clientAppId, tenantId, deploymentId);

        Assert.Equal("log:app-12345:00000000-0000-0000-0000-000000000001:gpt-4o", key);
        Assert.Contains(tenantId, key);
        Assert.Contains(clientAppId, key);
        Assert.Contains(deploymentId, key);
    }

    [Fact]
    public void RedisKeys_GenerateExpectedPatterns()
    {
        Assert.Equal("plan:abc", RedisKeys.Plan("abc"));
        Assert.Equal("client:app1:tenant1", RedisKeys.Client("app1", "tenant1"));
        Assert.Equal("traces:app1:tenant1", RedisKeys.Traces("app1", "tenant1"));
        Assert.Equal("pricing:gpt-4o", RedisKeys.Pricing("gpt-4o"));
        Assert.Equal("ratelimit:rpm:app1:tenant1:100", RedisKeys.RateLimitRpm("app1", "tenant1", 100));
        Assert.Equal("ratelimit:tpm:app1:tenant1:100", RedisKeys.RateLimitTpm("app1", "tenant1", 100));
    }
}
