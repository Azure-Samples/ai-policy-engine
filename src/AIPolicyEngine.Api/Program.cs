using System.Text.Json.Serialization;
using System.Security.Claims;
using System.Threading.Channels;
using Azure.Identity;
using AIPolicyEngine.Api.Endpoints;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, resilience
builder.AddServiceDefaults();

// ConfigureHTTP JSON options for minimal API model binding (enum as string, camelCase)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Redis via Aspire integration — uses Entra ID managed identity in Azure,
// falls back to password auth for local Aspire dev containers.
builder.AddRedisClient("redis", configureOptions: options =>
{
    if (string.IsNullOrEmpty(options.Password))
    {
        options.ConfigureForAzureWithTokenCredentialAsync(new DefaultAzureCredential())
            .GetAwaiter().GetResult();
    }
});

// Cosmos DB via Aspire integration (uses connection named "aipolicy" from AppHost)
builder.AddAzureCosmosClient("aipolicy", configureClientOptions: options =>
{
    options.UseSystemTextJsonSerializerWithOptions = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };
},
configureSettings: settings =>
{
    settings.Credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeVisualStudioCredential = true,
        ExcludeVisualStudioCodeCredential = true,
        ExcludeAzureCliCredential = true,
        ExcludeAzurePowerShellCredential = true,
        ExcludeAzureDeveloperCliCredential = true,
    });
});

// Register application services
builder.Services.AddSingleton<IChargebackCalculator, ChargebackCalculator>();
builder.Services.AddSingleton<ChargebackMetrics>();
builder.Services.AddSingleton<ILogDataService, LogDataService>();
builder.Services.AddSingleton<IAuditStore, AuditStore>();
builder.Services.AddSingleton<IDeploymentDiscoveryService, DeploymentDiscoveryService>();

// Repository pattern: Cosmos (source of truth) + Redis (cache layer)
builder.Services.AddSingleton<ConfigurationContainerProvider>();
builder.Services.AddSingleton<CosmosPlanRepository>();
builder.Services.AddSingleton<CosmosClientRepository>();
builder.Services.AddSingleton<CosmosPricingRepository>();
builder.Services.AddSingleton<CosmosUsagePolicyRepository>();
builder.Services.AddSingleton<CosmosRoutingPolicyRepository>();

builder.Services.AddSingleton<IRepository<PlanData>>(sp =>
    new CachedRepository<PlanData>(
        sp.GetRequiredService<CosmosPlanRepository>(),
        sp.GetRequiredService<IConnectionMultiplexer>(),
        id => RedisKeys.Plan(id),
        entity => entity.Id,
        sp.GetRequiredService<ILogger<CachedRepository<PlanData>>>()));

builder.Services.AddSingleton<IRepository<ClientPlanAssignment>>(sp =>
    new CachedRepository<ClientPlanAssignment>(
        sp.GetRequiredService<CosmosClientRepository>(),
        sp.GetRequiredService<IConnectionMultiplexer>(),
        id => $"client:{id}",
        entity => $"{entity.ClientAppId}:{entity.TenantId}",
        sp.GetRequiredService<ILogger<CachedRepository<ClientPlanAssignment>>>()));

builder.Services.AddSingleton<IRepository<ModelPricing>>(sp =>
    new CachedRepository<ModelPricing>(
        sp.GetRequiredService<CosmosPricingRepository>(),
        sp.GetRequiredService<IConnectionMultiplexer>(),
        id => RedisKeys.Pricing(id),
        entity => entity.ModelId,
        sp.GetRequiredService<ILogger<CachedRepository<ModelPricing>>>()));

builder.Services.AddSingleton<IRepository<UsagePolicySettings>>(sp =>
    new CachedRepository<UsagePolicySettings>(
        sp.GetRequiredService<CosmosUsagePolicyRepository>(),
        sp.GetRequiredService<IConnectionMultiplexer>(),
        id => $"settings:{id}",
        _ => "usage-policy",
        sp.GetRequiredService<ILogger<CachedRepository<UsagePolicySettings>>>()));

builder.Services.AddSingleton<IRepository<ModelRoutingPolicy>>(sp =>
    new CachedRepository<ModelRoutingPolicy>(
        sp.GetRequiredService<CosmosRoutingPolicyRepository>(),
        sp.GetRequiredService<IConnectionMultiplexer>(),
        id => RedisKeys.RoutingPolicy(id),
        entity => entity.Id,
        sp.GetRequiredService<ILogger<CachedRepository<ModelRoutingPolicy>>>()));

builder.Services.AddSingleton<IUsagePolicyStore, UsagePolicyStore>();

// Startup services: migration first, then cache warming (sequential, blocks app start)
builder.Services.AddHostedService<RedisToCosmosMigrationService>();
builder.Services.AddHostedService<CacheWarmingService>();

// Audit log channel + background writer for batched Cosmos DB writes
builder.Services.AddSingleton(Channel.CreateUnbounded<AuditLogItem>(
    new UnboundedChannelOptions { SingleReader = true }));
builder.Services.AddHostedService<AuditLogWriter>();

// OpenAPI support
builder.Services.AddOpenApi();

// Purview integration for DLP policy validation and audit emission (Agent 365)
builder.Services.AddPurviewServices(builder.Configuration);

// Authentication: supports AzureAd or Keycloak based on AuthProvider config
var authProvider = builder.Configuration.GetValue<string>("AuthProvider") ?? "AzureAd";

if (authProvider.Equals("Keycloak", StringComparison.OrdinalIgnoreCase))
{
    var keycloakSection = builder.Configuration.GetSection("Keycloak");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = keycloakSection["Authority"];
            options.Audience = keycloakSection["Audience"];
            options.RequireHttpsMetadata = keycloakSection.GetValue<bool>("RequireHttpsMetadata", true);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                // Keycloak uses "preferred_username" instead of "name"
                NameClaimType = "preferred_username",
                RoleClaimType = "roles"
            };
            // Extract roles from Keycloak's nested realm_access/resource_access claims
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    if (context.Principal?.Identity is ClaimsIdentity identity)
                    {
                        // Extract realm roles from realm_access.roles
                        var realmAccess = context.Principal.FindFirst("realm_access");
                        if (realmAccess != null)
                        {
                            try
                            {
                                var parsed = System.Text.Json.JsonDocument.Parse(realmAccess.Value);
                                if (parsed.RootElement.TryGetProperty("roles", out var roles))
                                {
                                    foreach (var role in roles.EnumerateArray())
                                    {
                                        var roleValue = role.GetString();
                                        if (!string.IsNullOrEmpty(roleValue))
                                            identity.AddClaim(new Claim("roles", roleValue));
                                    }
                                }
                            }
                            catch { /* ignore malformed claim */ }
                        }

                        // Extract client roles from resource_access.<clientId>.roles
                        var resourceAccess = context.Principal.FindFirst("resource_access");
                        if (resourceAccess != null)
                        {
                            try
                            {
                                var parsed = System.Text.Json.JsonDocument.Parse(resourceAccess.Value);
                                foreach (var client in parsed.RootElement.EnumerateObject())
                                {
                                    if (client.Value.TryGetProperty("roles", out var roles))
                                    {
                                        foreach (var role in roles.EnumerateArray())
                                        {
                                            var roleValue = role.GetString();
                                            if (!string.IsNullOrEmpty(roleValue))
                                                identity.AddClaim(new Claim("roles", roleValue));
                                        }
                                    }
                                }
                            }
                            catch { /* ignore malformed claim */ }
                        }
                    }
                    return Task.CompletedTask;
                }
            };
        });
}
else
{
    // Entra ID JWT Bearer authentication (default)
    builder.Services.AddAuthentication()
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("ExportPolicy", policy =>
        policy.RequireRole("AIPolicy.Export"))
    .AddPolicy("ApimPolicy", policy =>
        policy.RequireRole("AIPolicy.Apim"))
    .AddPolicy("AdminPolicy", policy =>
        policy.RequireRole("AIPolicy.Admin"))
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

// CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Aspire health check endpoints (anonymous for probes)
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

// Static files must be served before auth so the SPA (login page, JS, CSS) loads anonymously
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();

// Map all endpoints
app.MapAuthConfigEndpoints();
app.MapLogIngestEndpoints();
app.MapDashboardEndpoints();
app.MapPlanEndpoints();
app.MapExportEndpoints();
app.MapWebSocketEndpoints();
app.MapClientDetailEndpoints();
app.MapPrecheckEndpoints();
app.MapPricingEndpoints();
app.MapUsagePolicyEndpoints();
app.MapDeploymentEndpoints();
app.MapRoutingPolicyEndpoints();
app.MapRequestBillingEndpoints();

// SPA client-side routing fallback (anonymous — SPA handles its own auth)
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Make Program visible to benchmarks and tests
public partial class Program { }
