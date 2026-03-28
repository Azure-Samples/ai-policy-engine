using Azure.Core;
using Chargeback.Api.Models;
using Chargeback.Api.Services;
using Microsoft.Agents.AI.Purview;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chargeback.Tests;

public class PurviewServiceTests
{
    [Fact]
    public async Task NoOpPurviewAuditService_CompletesSuccessfully()
    {
        var service = new NoOpPurviewAuditService();

        await service.EmitAuditEventAsync(new LogIngestRequest());
        // Should complete without throwing
    }

    [Fact]
    public async Task NoOpPurviewAuditService_WithCancellationToken_CompletesSuccessfully()
    {
        var service = new NoOpPurviewAuditService();
        using var cts = new CancellationTokenSource();

        await service.EmitAuditEventAsync(new LogIngestRequest(), cts.Token);
    }

    [Fact]
    public void AddPurviewServices_WithoutConfig_RegistersNoOp()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddPurviewServices(config);

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IPurviewAuditService>();
        Assert.IsType<NoOpPurviewAuditService>(service);
    }

    [Fact]
    public void AddPurviewServices_WithEmptyPurviewClientAppId_RegistersNoOp()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PURVIEW_CLIENT_APP_ID"] = "",
            })
            .Build();

        services.AddPurviewServices(config);

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IPurviewAuditService>();
        Assert.IsType<NoOpPurviewAuditService>(service);
    }

    [Fact]
    public void AddPurviewServices_WithConfig_RegistersRealService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PURVIEW_CLIENT_APP_ID"] = "test-app-id",
                ["PURVIEW_APP_NAME"] = "Test App",
            })
            .Build();

        services.AddPurviewServices(config);

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IPurviewAuditService>();
        Assert.IsType<PurviewAuditService>(service);

        // Clean up the background processor
        if (service is IDisposable disposable)
            disposable.Dispose();
    }

    [Fact]
    public async Task PurviewAuditService_CanceledEmit_ReturnsCanceledTask()
    {
        var service = CreatePurviewAuditService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EmitAuditEventAsync(new LogIngestRequest(), cts.Token));

        await service.DisposeAsync();
    }

    [Fact]
    public async Task PurviewAuditService_ProcessesAndDisposesAsync()
    {
        var service = CreatePurviewAuditService();

        await service.EmitAuditEventAsync(new LogIngestRequest
        {
            TenantId = "tenant-1",
            ClientAppId = "client-1",
            DeploymentId = "gpt-4o",
            ResponseBody = new OpenAiResponseBody
            {
                Model = "gpt-4o",
                Usage = new UsageData { TotalTokens = 42 }
            }
        });

        await Task.Delay(25);
        await service.DisposeAsync();

        // No-op after disposal should not throw
        await service.EmitAuditEventAsync(new LogIngestRequest { TenantId = "tenant-1", ClientAppId = "client-1", DeploymentId = "gpt-4o" });
    }

    [Fact]
    public void PurviewAuditService_Dispose_IsIdempotent()
    {
        var service = CreatePurviewAuditService();

        service.Dispose();
        service.Dispose();
    }

    private static PurviewAuditService CreatePurviewAuditService()
    {
        var settings = new PurviewSettings("Test App")
        {
            IgnoreExceptions = true,
            PendingBackgroundJobLimit = 4
        };

        return new PurviewAuditService(
            settings,
            new StaticTokenCredential(),
            NullLogger<PurviewAuditService>.Instance);
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
