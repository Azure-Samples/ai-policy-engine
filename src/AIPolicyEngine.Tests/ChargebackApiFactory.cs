using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace AIPolicyEngine.Tests;

/// <summary>
/// Test authentication handler that authenticates requests carrying an Authorization header.
/// Requests without the header are treated as anonymous (returns NoResult), allowing
/// tests like "WithoutAuth_ReturnsUnauthorized" to continue working.
/// </summary>
internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("Authorization"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "test-apim"),
            new Claim(ClaimTypes.Role, "AIPolicy.Export"),
            new Claim(ClaimTypes.Role, "AIPolicy.Admin"),
            new Claim(ClaimTypes.Role, "AIPolicy.Apim"),
            new Claim("oid", "00000000-0000-0000-0000-000000000099"),
        };
        var identity = new ClaimsIdentity(claims, "TestScheme");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "TestScheme");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Custom WebApplicationFactory that replaces real Redis with an in-memory FakeRedis
/// and stubs Cosmos DB with a no-op IAuditStore for unit tests.
/// </summary>
public sealed class ChargebackApiFactory : WebApplicationFactory<Program>
{
    public FakeRedis Redis { get; } = new();
    public IAuditStore AuditStore { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Provide a dummy connection string so Aspire doesn't throw on missing config
                ["ConnectionStrings:redis"] = "localhost:99999,abortConnect=false,connectTimeout=1",
                // Dummy Cosmos connection for testing
                ["ConnectionStrings:aipolicy"] = "AccountEndpoint=https://localhost:8081/;AccountKey=dGVzdA==",
                // Disable AzureAd auth in tests
                ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd:TenantId"] = "test-tenant",
                ["AzureAd:ClientId"] = "test-client"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Remove all real IConnectionMultiplexer registrations (from Aspire)
            var descriptors = services
                .Where(d => d.ServiceType == typeof(IConnectionMultiplexer))
                .ToList();
            foreach (var d in descriptors) services.Remove(d);

            services.AddSingleton<IConnectionMultiplexer>(Redis.Multiplexer);

            // Replace ILogDataService with a fake that reads from FakeRedis
            var logDescriptors = services
                .Where(d => d.ServiceType == typeof(ILogDataService))
                .ToList();
            foreach (var d in logDescriptors) services.Remove(d);

            // Re-register the real LogDataService; it will use our fake IConnectionMultiplexer
            services.AddSingleton<ILogDataService, LogDataService>();

            // Remove real CosmosClient registrations and replace with stub
            RemoveService<CosmosClient>(services);
            var mockCosmos = Substitute.For<CosmosClient>();
            services.AddSingleton(mockCosmos);

            // Replace IAuditStore with a stub that returns empty collections
            RemoveService<IAuditStore>(services);
            var mockAuditStore = Substitute.For<IAuditStore>();
            mockAuditStore.GetAvailablePeriodsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<AIPolicyEngine.Api.Models.ExportPeriod>()));
            mockAuditStore.GetClientsForPeriodAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<AIPolicyEngine.Api.Models.ExportClient>()));
            mockAuditStore.GetBillingSummariesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<AIPolicyEngine.Api.Models.BillingSummaryDocument>()));
            mockAuditStore.GetClientAuditLogsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<AIPolicyEngine.Api.Models.AuditLogDocument>()));
            AuditStore = mockAuditStore;
            services.AddSingleton<IAuditStore>(mockAuditStore);

            // Remove Cosmos-dependent hosted services (no real Cosmos in tests)
            RemoveHostedService<RedisToCosmosMigrationService>(services);
            RemoveHostedService<CacheWarmingService>(services);

            // Replace IRepository<T> with Redis-backed test implementations
            // so tests that seed FakeRedis directly continue working.
            RemoveService<IRepository<PlanData>>(services);
            services.AddSingleton<IRepository<PlanData>>(
                new RedisBackedRepository<PlanData>(
                    Redis, id => RedisKeys.Plan(id), e => e.Id, RedisKeys.PlanPrefix));

            RemoveService<IRepository<ClientPlanAssignment>>(services);
            services.AddSingleton<IRepository<ClientPlanAssignment>>(
                new RedisBackedRepository<ClientPlanAssignment>(
                    Redis, id => $"client:{id}",
                    e => $"{e.ClientAppId}:{e.TenantId}", RedisKeys.ClientPrefix));

            RemoveService<IRepository<ModelPricing>>(services);
            services.AddSingleton<IRepository<ModelPricing>>(
                new RedisBackedRepository<ModelPricing>(
                    Redis, id => RedisKeys.Pricing(id), e => e.ModelId, RedisKeys.PricingPrefix));

            RemoveService<IRepository<UsagePolicySettings>>(services);
            services.AddSingleton<IRepository<UsagePolicySettings>>(
                new RedisBackedRepository<UsagePolicySettings>(
                    Redis, id => $"settings:{id}", _ => "usage-policy", "settings:*"));

            RemoveService<IRepository<ModelRoutingPolicy>>(services);
            services.AddSingleton<IRepository<ModelRoutingPolicy>>(
                new RedisBackedRepository<ModelRoutingPolicy>(
                    Redis, id => RedisKeys.RoutingPolicy(id), e => e.Id, RedisKeys.RoutingPolicyPrefix));

            // Replace authentication with a test handler that auto-authenticates
            services.AddAuthentication("TestScheme")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", _ => { });
            services.Configure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = "TestScheme");
            services.Configure<AuthenticationOptions>(o => o.DefaultChallengeScheme = "TestScheme");

            // Ensure the audit channel is registered (may already be)
            if (!services.Any(d => d.ServiceType == typeof(Channel<AuditLogItem>)))
            {
                services.AddSingleton(Channel.CreateUnbounded<AuditLogItem>(
                    new UnboundedChannelOptions { SingleReader = true }));
            }
        });
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }

    private static void RemoveHostedService<T>(IServiceCollection services) where T : class
    {
        var descriptors = services.Where(d =>
            d.ServiceType == typeof(IHostedService) &&
            (d.ImplementationType == typeof(T) ||
             (d.ImplementationFactory?.Method.ReturnType?.IsAssignableFrom(typeof(T)) ?? false))).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }

    /// <summary>
    /// IRepository backed directly by FakeRedis for test environments.
    /// Preserves the pre-migration behavior where endpoints read/wrote Redis directly,
    /// allowing tests that seed FakeRedis to continue working.
    /// </summary>
    private sealed class RedisBackedRepository<T>(
        FakeRedis redis,
        Func<string, string> keyFromId,
        Func<T, string> entityId,
        string scanPattern) : IRepository<T> where T : class
    {
        public async Task<T?> GetAsync(string id, CancellationToken ct = default)
        {
            var val = await redis.Database.StringGetAsync(keyFromId(id));
            if (!val.HasValue) return null;
            return JsonSerializer.Deserialize<T>((string)val!, JsonConfig.Default);
        }

        public async Task<List<T>> GetAllAsync(CancellationToken ct = default)
        {
            var keys = redis.Multiplexer.KeysFromAllServers(scanPattern);
            var results = new List<T>();
            foreach (var key in keys)
            {
                var val = await redis.Database.StringGetAsync(key);
                if (!val.HasValue) continue;
                var entity = JsonSerializer.Deserialize<T>((string)val!, JsonConfig.Default);
                if (entity is not null) results.Add(entity);
            }
            return results;
        }

        public async Task<T> UpsertAsync(T entity, CancellationToken ct = default)
        {
            var id = entityId(entity);
            var json = JsonSerializer.Serialize(entity, JsonConfig.Default);
            await redis.Database.StringSetAsync(keyFromId(id), json);
            return entity;
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            return await redis.Database.KeyDeleteAsync(keyFromId(id));
        }
    }
}
