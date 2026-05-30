using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using AIPolicyEngine.Api.Models.Apim;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Api.Services.ApimManagement;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIPolicyEngine.Tests.ApimManagement;

public sealed class ApimManagementEndpointTests : IClassFixture<ChargebackApiFactory>
{
    private readonly ChargebackApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = JsonConfig.Default;

    public ApimManagementEndpointTests(ChargebackApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", "/api/apim/apis")]
    [InlineData("GET", "/api/apim/apis/api-1/operations")]
    [InlineData("GET", "/api/apim/apis/api-1/policy")]
    [InlineData("GET", "/api/apim/apis/api-1/operations/chat/policy")]
    [InlineData("GET", "/api/apim/templates")]
    [InlineData("POST", "/api/apim/apis/api-1/policy")]
    [InlineData("POST", "/api/apim/apis/api-1/operations/chat/policy")]
    [InlineData("DELETE", "/api/apim/apis/api-1/policy")]
    [InlineData("DELETE", "/api/apim/apis/api-1/operations/chat/policy")]
    public async Task Endpoints_WithoutAuth_Return401(string method, string path)
    {
        using var client = CreateClient(authenticated: false);
        using var request = CreateRequest(method, path);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/apim/apis")]
    [InlineData("GET", "/api/apim/apis/api-1/operations")]
    [InlineData("GET", "/api/apim/apis/api-1/policy")]
    [InlineData("GET", "/api/apim/apis/api-1/operations/chat/policy")]
    [InlineData("GET", "/api/apim/templates")]
    [InlineData("POST", "/api/apim/apis/api-1/policy")]
    [InlineData("POST", "/api/apim/apis/api-1/operations/chat/policy")]
    [InlineData("DELETE", "/api/apim/apis/api-1/policy")]
    [InlineData("DELETE", "/api/apim/apis/api-1/operations/chat/policy")]
    public async Task Endpoints_WithoutAdminRole_Return403(string method, string path)
    {
        using var client = CreateClient(includeAdminRole: false);
        using var request = CreateRequest(method, path);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListApis_ReturnsConfiguredApis()
    {
        var catalog = CreateDefaultCatalog();
        using var client = CreateClient(catalog: catalog);

        var response = await client.GetAsync("/api/apim/apis");
        var payload = await response.Content.ReadFromJsonAsync<ApimApisResponse>(JsonOpts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        var api = Assert.Single(payload!.Apis);
        Assert.Equal("api-1", api.Id);
        Assert.Equal("Azure OpenAI API", api.DisplayName);
        Assert.Equal("openai", api.Path);
        Assert.True(api.IsCurrent);
    }

    [Fact]
    public async Task ListOperations_WhenApiMissing_Returns404()
    {
        using var client = CreateClient(catalog: new TestApimCatalogService());

        var response = await client.GetAsync("/api/apim/apis/missing/operations");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListOperations_WhenApiExists_ReturnsOperations()
    {
        var catalog = CreateDefaultCatalog();
        using var client = CreateClient(catalog: catalog);

        var response = await client.GetAsync("/api/apim/apis/api-1/operations");
        var payload = await response.Content.ReadFromJsonAsync<ApimOperationsResponse>(JsonOpts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        var operation = Assert.Single(payload!.Operations);
        Assert.Equal("chat", operation.Id);
        Assert.Equal("POST", operation.Method);
    }

    [Fact]
    public async Task GetApiPolicy_ReturnsAssignmentAndCurrentXml()
    {
        var catalog = CreateDefaultCatalog();
        var repo = new InMemoryPolicyAssignmentRepository();
        await repo.UpsertAsync(CreateAssignment(apiId: "api-1"));
        using var client = CreateClient(catalog: catalog, repo: repo);

        var response = await client.GetAsync("/api/apim/apis/api-1/policy");
        var payload = await response.Content.ReadFromJsonAsync<ApimPolicyResponse>(JsonOpts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.NotNull(payload!.Assignment);
        Assert.Equal("pa:api-1:_all", payload.Assignment!.Id);
        Assert.Equal("<policies><inbound><base /></inbound></policies>", payload.CurrentXml);
    }

    [Fact]
    public async Task GetApiPolicy_WhenApiMissing_Returns404()
    {
        using var client = CreateClient(catalog: new TestApimCatalogService());

        var response = await client.GetAsync("/api/apim/apis/missing/policy");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationPolicy_ReturnsAssignmentAndCurrentXml()
    {
        var catalog = CreateDefaultCatalog();
        var repo = new InMemoryPolicyAssignmentRepository();
        await repo.UpsertAsync(CreateAssignment(apiId: "api-1", operationId: "chat"));
        using var client = CreateClient(catalog: catalog, repo: repo);

        var response = await client.GetAsync("/api/apim/apis/api-1/operations/chat/policy");
        var payload = await response.Content.ReadFromJsonAsync<ApimPolicyResponse>(JsonOpts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.NotNull(payload!.Assignment);
        Assert.Equal("pa:api-1:chat", payload.Assignment!.Id);
        Assert.Equal("<policies><inbound><base /></inbound><backend><base /></backend></policies>", payload.CurrentXml);
    }

    [Fact]
    public async Task GetOperationPolicy_WhenOperationMissing_Returns404()
    {
        var catalog = CreateDefaultCatalog();
        catalog.Operations.Clear();
        using var client = CreateClient(catalog: catalog);

        var response = await client.GetAsync("/api/apim/apis/api-1/operations/missing/policy");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListTemplates_ReturnsShippedTemplates()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/apim/templates");
        var payload = await response.Content.ReadFromJsonAsync<TemplateListResponse>(JsonOpts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal(8, payload!.Templates.Count);
        Assert.Contains(payload.Templates, template => template.Id == "entra-jwt-rest");
        Assert.Contains(payload.Templates, template => template.Id == "keycloak-jwt-ai");
        Assert.Contains(payload.Templates, template => template.Id == "keycloak-jwt-ai-dlp");
        Assert.Contains(payload.Templates, template => template.Id == "keycloak-jwt-rest");
    }

    [Fact]
    public async Task ApplyApiPolicy_ValidBody_Returns202AndStoresPendingAssignment()
    {
        var catalog = CreateDefaultCatalog();
        var repo = new InMemoryPolicyAssignmentRepository();
        using var client = CreateClient(catalog: catalog, repo: repo);

        var response = await client.PostAsJsonAsync("/api/apim/apis/api-1/policy", CreateValidApplyRequest(), JsonOpts);
        var payload = await response.Content.ReadFromJsonAsync<ApplyPolicyAcceptedResponse>(JsonOpts);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("pa:api-1:_all", payload!.AssignmentId);
        Assert.Equal(PolicyAssignmentStatuses.Pending, (await repo.GetAsync("api-1", null))!.Status);
    }

    [Fact]
    public async Task ApplyApiPolicy_MissingTemplateId_Returns400()
    {
        var catalog = CreateDefaultCatalog();
        using var client = CreateClient(catalog: catalog);

        var response = await client.PostAsJsonAsync("/api/apim/apis/api-1/policy", new ApplyPolicyRequest(), JsonOpts);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApplyApiPolicy_MissingRequiredParameter_Returns400()
    {
        var catalog = CreateDefaultCatalog();
        using var client = CreateClient(catalog: catalog);

        var response = await client.PostAsJsonAsync("/api/apim/apis/api-1/policy", new ApplyPolicyRequest
        {
            TemplateId = "entra-jwt-ai",
            Parameters = new Dictionary<string, JsonElement>
            {
                ["ExpectedAudience"] = ToJson("api://engine")
            }
        }, JsonOpts);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApplyApiPolicy_WhenApiMissing_Returns404()
    {
        using var client = CreateClient(catalog: new TestApimCatalogService());

        var response = await client.PostAsJsonAsync("/api/apim/apis/missing/policy", CreateValidApplyRequest(), JsonOpts);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApplyOperationPolicy_ValidBody_Returns202AndStoresPendingAssignment()
    {
        var catalog = CreateDefaultCatalog();
        var repo = new InMemoryPolicyAssignmentRepository();
        using var client = CreateClient(catalog: catalog, repo: repo);

        var response = await client.PostAsJsonAsync("/api/apim/apis/api-1/operations/chat/policy", CreateValidApplyRequest(), JsonOpts);
        var payload = await response.Content.ReadFromJsonAsync<ApplyPolicyAcceptedResponse>(JsonOpts);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("pa:api-1:chat", payload!.AssignmentId);
        Assert.Equal(PolicyAssignmentStatuses.Pending, (await repo.GetAsync("api-1", "chat"))!.Status);
    }

    [Fact]
    public async Task ApplyOperationPolicy_WhenOperationMissing_Returns404()
    {
        var catalog = CreateDefaultCatalog();
        catalog.Operations.Clear();
        using var client = CreateClient(catalog: catalog);

        var response = await client.PostAsJsonAsync("/api/apim/apis/api-1/operations/missing/policy", CreateValidApplyRequest(), JsonOpts);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ClearApiPolicy_Returns200AndDeletesAssignment()
    {
        var catalog = CreateDefaultCatalog();
        var repo = new InMemoryPolicyAssignmentRepository();
        await repo.UpsertAsync(CreateAssignment(apiId: "api-1"));
        using var client = CreateClient(catalog: catalog, repo: repo);

        var response = await client.DeleteAsync("/api/apim/apis/api-1/policy");
        var payload = await response.Content.ReadFromJsonAsync<ClearPolicyResponse>(JsonOpts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("cleared", payload!.Status);
        Assert.Null(await repo.GetAsync("api-1", null));
        Assert.Contains("api-1", catalog.ClearedApiIds);
    }

    [Fact]
    public async Task ClearApiPolicy_WhenApiMissing_Returns404()
    {
        using var client = CreateClient(catalog: new TestApimCatalogService());

        var response = await client.DeleteAsync("/api/apim/apis/missing/policy");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ClearOperationPolicy_Returns200AndDeletesAssignment()
    {
        var catalog = CreateDefaultCatalog();
        var repo = new InMemoryPolicyAssignmentRepository();
        await repo.UpsertAsync(CreateAssignment(apiId: "api-1", operationId: "chat"));
        using var client = CreateClient(catalog: catalog, repo: repo);

        var response = await client.DeleteAsync("/api/apim/apis/api-1/operations/chat/policy");
        var payload = await response.Content.ReadFromJsonAsync<ClearPolicyResponse>(JsonOpts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("cleared", payload!.Status);
        Assert.Null(await repo.GetAsync("api-1", "chat"));
        Assert.Contains(("api-1", "chat"), catalog.ClearedOperations);
    }

    [Fact]
    public async Task ClearOperationPolicy_WhenOperationMissing_Returns404()
    {
        var catalog = CreateDefaultCatalog();
        catalog.Operations.Clear();
        using var client = CreateClient(catalog: catalog);

        var response = await client.DeleteAsync("/api/apim/apis/api-1/operations/missing/policy");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient CreateClient(
        TestApimCatalogService? catalog = null,
        InMemoryPolicyAssignmentRepository? repo = null,
        bool authenticated = true,
        bool includeAdminRole = true)
    {
        catalog ??= CreateDefaultCatalog();
        repo ??= new InMemoryPolicyAssignmentRepository();

        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                RemoveService<IApimCatalogService>(services);
                services.AddSingleton<IApimCatalogService>(catalog);

                RemoveService<IPolicyAssignmentRepository>(services);
                services.AddSingleton<IPolicyAssignmentRepository>(repo);

                RemoveService<Channel<ApimPolicyApplyWorkItem>>(services);
                services.AddSingleton(Channel.CreateUnbounded<ApimPolicyApplyWorkItem>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                }));

                RemoveHostedService<ApimPolicyApplyBackgroundService>(services);

                if (!includeAdminRole)
                {
                    services.AddAuthentication("LimitedScheme")
                        .AddScheme<AuthenticationSchemeOptions, LimitedTestAuthHandler>("LimitedScheme", _ => { });
                    services.Configure<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = "LimitedScheme";
                        options.DefaultChallengeScheme = "LimitedScheme";
                    });
                }
            });
        });

        var client = factory.CreateClient();
        if (authenticated)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
        }

        return client;
    }

    private static HttpRequestMessage CreateRequest(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = JsonContent.Create(CreateValidApplyRequest(), options: JsonOpts);
        }

        return request;
    }

    private static ApplyPolicyRequest CreateValidApplyRequest() => new()
    {
        TemplateId = "entra-jwt-ai",
        Parameters = new Dictionary<string, JsonElement>
        {
            ["ExpectedAudience"] = ToJson("api://engine"),
            ["ContainerAppUrl"] = ToJson("https://engine.contoso.com"),
            ["ContainerAppAudience"] = ToJson("api://policy-engine")
        }
    };

    private static TestApimCatalogService CreateDefaultCatalog()
    {
        var catalog = new TestApimCatalogService();
        catalog.Apis["api-1"] = new ApimApiSummary
        {
            Id = "api-1",
            DisplayName = "Azure OpenAI API",
            Path = "openai",
            ServiceUrl = "https://contoso.openai.azure.com",
            IsCurrent = true
        };
        catalog.Operations[("api-1", "chat")] = new ApimOperationSummary
        {
            Id = "chat",
            DisplayName = "Chat Completions",
            Method = "POST",
            UrlTemplate = "/deployments/{deploymentId}/chat/completions"
        };
        catalog.ApiPolicies["api-1"] = "<policies><inbound><base /></inbound></policies>";
        catalog.OperationPolicies[("api-1", "chat")] = "<policies><inbound><base /></inbound><backend><base /></backend></policies>";
        return catalog;
    }

    private static PolicyAssignment CreateAssignment(string apiId, string? operationId = null) => new()
    {
        ApiId = apiId,
        OperationId = operationId,
        ApiDisplayName = "Azure OpenAI API",
        TemplateId = "entra-jwt-ai",
        TemplateVersion = "1.0",
        Parameters = new Dictionary<string, JsonElement>
        {
            ["ExpectedAudience"] = ToJson("api://engine"),
            ["ContainerAppUrl"] = ToJson("https://engine.contoso.com"),
            ["ContainerAppAudience"] = ToJson("api://policy-engine")
        },
        AppliedBy = "tester@contoso.com",
        Status = PolicyAssignmentStatuses.Synced,
        CreatedAt = new DateTime(2026, 05, 21, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 05, 21, 12, 0, 0, DateTimeKind.Utc)
    };

    private static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOpts);

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }

    private static void RemoveHostedService<T>(IServiceCollection services) where T : class
    {
        var descriptors = services.Where(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            (descriptor.ImplementationType == typeof(T) ||
             (descriptor.ImplementationFactory?.Method.ReturnType?.IsAssignableFrom(typeof(T)) ?? false))).ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }

    private sealed class LimitedTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public LimitedTestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "limited-user"),
                new Claim(ClaimTypes.Role, "AIPolicy.Export")
            };
            var identity = new ClaimsIdentity(claims, "LimitedScheme");
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "LimitedScheme")));
        }
    }

    private sealed class TestApimCatalogService : IApimCatalogService
    {
        public Dictionary<string, ApimApiSummary> Apis { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string ApiId, string OperationId), ApimOperationSummary> Operations { get; } = new();
        public Dictionary<string, string> ApiPolicies { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string ApiId, string OperationId), string> OperationPolicies { get; } = new();
        public List<string> ClearedApiIds { get; } = [];
        public List<(string ApiId, string OperationId)> ClearedOperations { get; } = [];

        public Task<IReadOnlyList<ApimApiSummary>> ListApisAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ApimApiSummary>>(Apis.Values.ToList());

        public Task<ApimApiSummary?> GetApiAsync(string apiId, CancellationToken ct = default)
            => Task.FromResult(Apis.TryGetValue(apiId, out var api) ? api : null);

        public Task<IReadOnlyList<ApimOperationSummary>> ListOperationsAsync(string apiId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ApimOperationSummary>>(Operations
                .Where(pair => pair.Key.ApiId == apiId)
                .Select(pair => pair.Value)
                .ToList());

        public Task<ApimOperationSummary?> GetOperationAsync(string apiId, string operationId, CancellationToken ct = default)
            => Task.FromResult(Operations.TryGetValue((apiId, operationId), out var operation) ? operation : null);

        public Task<string?> GetApiPolicyXmlAsync(string apiId, CancellationToken ct = default)
            => Task.FromResult(ApiPolicies.TryGetValue(apiId, out var xml) ? xml : null);

        public Task<string?> GetOperationPolicyXmlAsync(string apiId, string operationId, CancellationToken ct = default)
            => Task.FromResult(OperationPolicies.TryGetValue((apiId, operationId), out var xml) ? xml : null);

        public Task ApplyApiPolicyAsync(string apiId, string xml, CancellationToken ct = default)
        {
            ApiPolicies[apiId] = xml;
            return Task.CompletedTask;
        }

        public Task ApplyOperationPolicyAsync(string apiId, string operationId, string xml, CancellationToken ct = default)
        {
            OperationPolicies[(apiId, operationId)] = xml;
            return Task.CompletedTask;
        }

        public Task SetApiPassthroughPolicyAsync(string apiId, CancellationToken ct = default)
        {
            ClearedApiIds.Add(apiId);
            ApiPolicies[apiId] = "<policies><inbound><base /></inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>";
            return Task.CompletedTask;
        }

        public Task SetOperationPassthroughPolicyAsync(string apiId, string operationId, CancellationToken ct = default)
        {
            ClearedOperations.Add((apiId, operationId));
            OperationPolicies[(apiId, operationId)] = "<policies><inbound><base /></inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>";
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryPolicyAssignmentRepository : IPolicyAssignmentRepository
    {
        private readonly Dictionary<string, PolicyAssignment> _store = new(StringComparer.Ordinal);

        public Task<PolicyAssignment?> GetAsync(string apiId, string? operationId, CancellationToken ct = default)
        {
            var id = PolicyAssignment.BuildId(apiId, operationId);
            return Task.FromResult(_store.TryGetValue(id, out var assignment) ? Clone(assignment) : null);
        }

        public Task<List<PolicyAssignment>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(_store.Values.Select(Clone).ToList());

        public Task<PolicyAssignment> UpsertAsync(PolicyAssignment assignment, CancellationToken ct = default)
        {
            assignment.Id = PolicyAssignment.BuildId(assignment.ApiId, assignment.OperationId);
            assignment.PartitionKey = "policy-assignment";
            var clone = Clone(assignment);
            _store[clone.Id] = clone;
            return Task.FromResult(Clone(clone));
        }

        public Task<bool> DeleteAsync(string apiId, string? operationId, CancellationToken ct = default)
            => Task.FromResult(_store.Remove(PolicyAssignment.BuildId(apiId, operationId)));

        private static PolicyAssignment Clone(PolicyAssignment assignment)
            => JsonSerializer.Deserialize<PolicyAssignment>(JsonSerializer.Serialize(assignment, JsonConfig.Default), JsonConfig.Default)!;
    }
}
