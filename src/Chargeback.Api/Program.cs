using System.Threading.Channels;
using Azure.Identity;
using Chargeback.Api.Endpoints;
using Chargeback.Api.Models;
using Chargeback.Api.Services;
using Microsoft.Identity.Web;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, resilience
builder.AddServiceDefaults();

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

// Cosmos DB via Aspire integration (uses connection named "chargeback" from AppHost)
builder.AddAzureCosmosClient("chargeback", configureClientOptions: options =>
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
builder.Services.AddSingleton<IUsagePolicyStore, UsagePolicyStore>();
builder.Services.AddSingleton<IAuditStore, AuditStore>();
builder.Services.AddSingleton<IDeploymentDiscoveryService, DeploymentDiscoveryService>();

// Audit log channel + background writer for batched Cosmos DB writes
builder.Services.AddSingleton(Channel.CreateUnbounded<AuditLogItem>(
    new UnboundedChannelOptions { SingleReader = true }));
builder.Services.AddHostedService<AuditLogWriter>();

// OpenAPI support
builder.Services.AddOpenApi();

// Purview integration for DLP policy validation and audit emission (Agent 365)
builder.Services.AddPurviewServices(builder.Configuration);

// Entra ID JWT Bearer authentication
builder.Services.AddAuthentication()
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("ExportPolicy", policy =>
        policy.RequireRole("Chargeback.Export"))
    .AddPolicy("ApimPolicy", policy =>
        policy.RequireRole("Chargeback.Apim"))
    .AddPolicy("AdminPolicy", policy =>
        policy.RequireRole("Chargeback.Admin"))
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

// SPA client-side routing fallback (anonymous — SPA handles its own auth)
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Make Program visible to benchmarks and tests
public partial class Program { }
