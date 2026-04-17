using Azure.Core;
using Chargeback.Api.Models;
using Chargeback.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Chargeback.Tests;

public class Agent365ServiceTests
{
    // ------------------------------------------------------------------ //
    //  NoOpAgent365ObservabilityService
    // ------------------------------------------------------------------ //

    [Fact]
    public void NoOpAgent365ObservabilityService_StartInvokeAgentScope_ReturnsNull()
    {
        var service = new NoOpAgent365ObservabilityService();

        var scope = service.StartInvokeAgentScope(
            clientAppId: "test-app-id",
            tenantId: "test-tenant-id",
            clientDisplayName: "Test App",
            correlationId: "correlation-123",
            promptContent: "test prompt");

        Assert.Null(scope);
    }

    [Fact]
    public void NoOpAgent365ObservabilityService_StartInferenceScope_ReturnsNull()
    {
        var service = new NoOpAgent365ObservabilityService();

        var request = new LogIngestRequest
        {
            ClientAppId = "test-app-id",
            TenantId = "test-tenant-id"
        };

        var scope = service.StartInferenceScope(request, "Test App");

        Assert.Null(scope);
    }

    // ------------------------------------------------------------------ //
    //  DI registration
    // ------------------------------------------------------------------ //

    [Fact]
    public void AddAgent365Observability_WithoutConfig_RegistersNoOp()
    {
        var builder = CreateHostBuilder();
        
        builder.AddAgent365Observability();

        var provider = builder.Services.BuildServiceProvider();
        var service = provider.GetRequiredService<IAgent365ObservabilityService>();
        
        Assert.IsType<NoOpAgent365ObservabilityService>(service);
    }

    [Fact]
    public void AddAgent365Observability_WithFalseConfig_RegistersNoOp()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            ["ENABLE_A365_OBSERVABILITY_EXPORTER"] = "false"
        });

        builder.AddAgent365Observability();

        var provider = builder.Services.BuildServiceProvider();
        var service = provider.GetRequiredService<IAgent365ObservabilityService>();
        
        Assert.IsType<NoOpAgent365ObservabilityService>(service);
    }

    [Fact]
    public void AddAgent365Observability_WithInvalidConfig_RegistersNoOp()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            ["ENABLE_A365_OBSERVABILITY_EXPORTER"] = "not-a-bool"
        });

        builder.AddAgent365Observability();

        var provider = builder.Services.BuildServiceProvider();
        var service = provider.GetRequiredService<IAgent365ObservabilityService>();
        
        Assert.IsType<NoOpAgent365ObservabilityService>(service);
    }

    [Fact]
    public void AddAgent365Observability_WithExporterEnabled_RegistersRealService()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            ["ENABLE_A365_OBSERVABILITY_EXPORTER"] = "true"
        });

        builder.AddAgent365Observability();

        var provider = builder.Services.BuildServiceProvider();
        var service = provider.GetRequiredService<IAgent365ObservabilityService>();
        
        Assert.IsType<Agent365ObservabilityService>(service);
    }

    // ------------------------------------------------------------------ //
    //  Agent365ObservabilityService (stub implementation)
    // ------------------------------------------------------------------ //

    [Fact]
    public void Agent365ObservabilityService_StartInvokeAgentScope_ReturnsNullStub()
    {
        var credential = Substitute.For<TokenCredential>();
        var logger = Substitute.For<ILogger<Agent365ObservabilityService>>();
        var service = new Agent365ObservabilityService(credential, logger);

        var scope = service.StartInvokeAgentScope(
            clientAppId: "test-app-id",
            tenantId: "test-tenant-id",
            clientDisplayName: "Test App",
            correlationId: "correlation-123",
            promptContent: "test prompt");

        Assert.Null(scope);
    }

    [Fact]
    public void Agent365ObservabilityService_StartInferenceScope_ReturnsNullStub()
    {
        var credential = Substitute.For<TokenCredential>();
        var logger = Substitute.For<ILogger<Agent365ObservabilityService>>();
        var service = new Agent365ObservabilityService(credential, logger);

        var request = new LogIngestRequest
        {
            ClientAppId = "test-app-id",
            TenantId = "test-tenant-id"
        };

        var scope = service.StartInferenceScope(request, "Test App");

        Assert.Null(scope);
    }

    [Fact]
    public void Agent365ObservabilityService_StartInvokeAgentScope_WithNullOptionalParams_ReturnsNullStub()
    {
        var credential = Substitute.For<TokenCredential>();
        var logger = Substitute.For<ILogger<Agent365ObservabilityService>>();
        var service = new Agent365ObservabilityService(credential, logger);

        var scope = service.StartInvokeAgentScope(
            clientAppId: "test-app-id",
            tenantId: "test-tenant-id",
            clientDisplayName: null,
            correlationId: null,
            promptContent: null);

        Assert.Null(scope);
    }

    [Fact]
    public void Agent365ObservabilityService_StartInferenceScope_WithNullDisplayName_ReturnsNullStub()
    {
        var credential = Substitute.For<TokenCredential>();
        var logger = Substitute.For<ILogger<Agent365ObservabilityService>>();
        var service = new Agent365ObservabilityService(credential, logger);

        var request = new LogIngestRequest
        {
            ClientAppId = "test-app-id",
            TenantId = "test-tenant-id"
        };

        var scope = service.StartInferenceScope(request, clientDisplayName: null);

        Assert.Null(scope);
    }

    // ------------------------------------------------------------------ //
    //  Helper methods
    // ------------------------------------------------------------------ //

    private static IHostApplicationBuilder CreateHostBuilder(
        Dictionary<string, string?>? configValues = null)
    {
        var configBuilder = new ConfigurationBuilder();
        
        if (configValues != null)
        {
            configBuilder.AddInMemoryCollection(configValues);
        }

        var configuration = configBuilder.Build();

        var hostBuilder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        // Replace the configuration with our test configuration
        hostBuilder.Configuration.AddConfiguration(configuration);

        // Add required services
        hostBuilder.Services.AddLogging();
        hostBuilder.Services.AddSingleton<TokenCredential>(Substitute.For<TokenCredential>());

        return hostBuilder;
    }
}
