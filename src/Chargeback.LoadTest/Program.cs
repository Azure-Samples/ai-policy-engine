using System.Text;
using System.Text.Json;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using NBomber.Http.CSharp;

var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5057";

Console.WriteLine($"Load testing: {baseUrl}");
Console.WriteLine();

using var httpClient = Http.CreateDefaultClient();

// --- Scenario 1: Precheck throughput ---
var precheckScenario = Scenario.Create("precheck", async context =>
{
    var clientAppId = Environment.GetEnvironmentVariable("LOADTEST_CLIENT_APP_ID") ?? "00000000-0000-0000-0000-000000000001";
    var tenantId = Environment.GetEnvironmentVariable("LOADTEST_TENANT_ID") ?? "00000000-0000-0000-0000-000000000000";
    var request = Http.CreateRequest("GET", $"{baseUrl}/api/precheck/{clientAppId}/{tenantId}?deploymentId=gpt-4o");
    var response = await Http.Send(httpClient, request);
    return response;
})
.WithLoadSimulations(
    Simulation.Inject(rate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
);

// --- Scenario 2: Log ingest throughput ---
var testTenantId = Environment.GetEnvironmentVariable("LOADTEST_TENANT_ID") ?? "00000000-0000-0000-0000-000000000000";
var testClientAppId = Environment.GetEnvironmentVariable("LOADTEST_CLIENT_APP_ID") ?? "00000000-0000-0000-0000-000000000001";
var logBody = JsonSerializer.Serialize(new
{
    tenantId = testTenantId,
    clientAppId = testClientAppId,
    audience = "api://test",
    deploymentId = "gpt-4o",
    responseBody = new
    {
        model = "gpt-4o",
        @object = "chat.completion",
        usage = new Dictionary<string, int>
        {
            ["prompt_tokens"] = 50,
            ["completion_tokens"] = 25,
            ["total_tokens"] = 75
        }
    }
});

var ingestScenario = Scenario.Create("log_ingest", async context =>
{
    var request = Http.CreateRequest("POST", $"{baseUrl}/api/log")
        .WithHeader("Content-Type", "application/json")
        .WithBody(new StringContent(logBody, Encoding.UTF8, "application/json"));
    var response = await Http.Send(httpClient, request);
    return response;
})
.WithLoadSimulations(
    Simulation.Inject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
);

// --- Scenario 3: Dashboard read throughput ---
var dashboardScenario = Scenario.Create("dashboard_read", async context =>
{
    var request = Http.CreateRequest("GET", $"{baseUrl}/chargeback");
    var response = await Http.Send(httpClient, request);
    return response;
})
.WithLoadSimulations(
    Simulation.Inject(rate: 20, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
);

// --- Scenario 4: Combined APIM flow (precheck then log) ---
var combinedScenario = Scenario.Create("apim_flow", async context =>
{
    var precheck = Http.CreateRequest("GET", $"{baseUrl}/api/precheck/{testClientAppId}/{testTenantId}?deploymentId=gpt-4o");
    var precheckResp = await Http.Send(httpClient, precheck);
    if (precheckResp.IsError) return precheckResp;

    var log = Http.CreateRequest("POST", $"{baseUrl}/api/log")
        .WithHeader("Content-Type", "application/json")
        .WithBody(new StringContent(logBody, Encoding.UTF8, "application/json"));
    var logResp = await Http.Send(httpClient, log);
    return logResp;
})
.WithLoadSimulations(
    Simulation.Inject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
);

NBomberRunner
    .RegisterScenarios(precheckScenario, ingestScenario, dashboardScenario, combinedScenario)
    .WithReportFolder("reports")
    .WithReportFormats(ReportFormat.Html, ReportFormat.Md)
    .Run();
