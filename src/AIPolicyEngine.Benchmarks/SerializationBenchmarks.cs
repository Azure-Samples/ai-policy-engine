using System.Text.Json;
using BenchmarkDotNet.Attributes;
using AIPolicyEngine.Api.Models;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class SerializationBenchmarks
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private LogIngestRequest _request = null!;
    private string _serializedRequest = null!;
    private CachedLogData _cachedData = null!;
    private string _serializedCachedData = null!;

    [GlobalSetup]
    public void Setup()
    {
        _request = new LogIngestRequest
        {
            TenantId = "00000000-0000-0000-0000-000000000001",
            ClientAppId = "00000000-0000-0000-0000-000000000002",
            Audience = "api://test",
            DeploymentId = "gpt-4o",
            ResponseBody = new OpenAiResponseBody
            {
                Model = "gpt-4o",
                Object = "chat.completion",
                Usage = new UsageData { PromptTokens = 500, CompletionTokens = 200, TotalTokens = 700 }
            }
        };
        _serializedRequest = JsonSerializer.Serialize(_request, JsonOptions);

        _cachedData = new CachedLogData
        {
            TenantId = "00000000-0000-0000-0000-000000000001",
            ClientAppId = "00000000-0000-0000-0000-000000000002",
            DeploymentId = "gpt-4o",
            Model = "gpt-4o",
            PromptTokens = 5000,
            CompletionTokens = 2000,
            TotalTokens = 7000
        };
        _serializedCachedData = JsonSerializer.Serialize(_cachedData, JsonOptions);
    }

    [Benchmark]
    public string SerializeLogIngestRequest() => JsonSerializer.Serialize(_request, JsonOptions);

    [Benchmark]
    public LogIngestRequest? DeserializeLogIngestRequest() => JsonSerializer.Deserialize<LogIngestRequest>(_serializedRequest, JsonOptions);

    [Benchmark]
    public string SerializeCachedLogData() => JsonSerializer.Serialize(_cachedData, JsonOptions);

    [Benchmark]
    public CachedLogData? DeserializeCachedLogData() => JsonSerializer.Deserialize<CachedLogData>(_serializedCachedData, JsonOptions);
}
