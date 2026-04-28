using System.Net.Http.Json;
using BenchmarkDotNet.Attributes;
using AIPolicyEngine.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class EndpointBenchmarks
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    [GlobalSetup]
    public void Setup()
    {
        // Note: This requires Redis to be running. Skip if Redis not available.
        try
        {
            _factory = new WebApplicationFactory<Program>();
            _client = _factory.CreateClient();
        }
        catch
        {
            // Redis not available — benchmarks will be skipped
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Benchmark]
    public async Task<HttpResponseMessage?> GetUsageSummary()
    {
        if (_client is null) return null;
        return await _client.GetAsync("/api/usage");
    }

    [Benchmark]
    public async Task<HttpResponseMessage?> PostLogIngest()
    {
        if (_client is null) return null;
        var request = new LogIngestRequest
        {
            TenantId = "bench-tenant",
            ClientAppId = "bench-client",
            DeploymentId = "gpt-4o",
            ResponseBody = new OpenAiResponseBody
            {
                Model = "gpt-4o",
                Object = "chat.completion",
                Usage = new UsageData { PromptTokens = 50, CompletionTokens = 25, TotalTokens = 75 }
            }
        };
        return await _client.PostAsJsonAsync("/api/log", request);
    }
}
