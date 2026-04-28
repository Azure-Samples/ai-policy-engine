#:package DotNetEnv@3.1.1
#:package Microsoft.Agents.AI@1.0.0-rc2
#:package Microsoft.Extensions.Configuration.EnvironmentVariables@10.0.3
#:package Microsoft.Extensions.Configuration.UserSecrets@10.0.3
#:package Microsoft.Identity.Client@4.82.1

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using DotNetEnv;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;

LoadEnvironmentFiles();

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

DemoClientSettings settings;
try
{
    settings = DemoClientSettings.FromConfiguration(configuration);
}
catch (InvalidOperationException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Configuration error: {ex.Message}");
    Console.ResetColor();
    return;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Azure AIPolicyEngine Demo —     Agent Framework         ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine();

foreach (var client in settings.Clients)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"━━━ {client.Name} ━━━");
    Console.WriteLine($"    App ID:    {client.AppId}");
    Console.WriteLine($"    Tenant ID: {client.TenantId}");
    Console.WriteLine($"    Plan:      {client.Plan}");
    Console.WriteLine($"    Agent:     {client.Name} Agent");
    Console.ResetColor();
    Console.WriteLine();

    Console.Write("  Authenticating with Entra ID... ");
    var token = await AcquireAccessTokenAsync(client, client.TenantId, settings.ApiScope);
    if (string.IsNullOrWhiteSpace(token))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("✗ Auth failed");
        Console.ResetColor();
        Console.WriteLine("  (Skipping this client)");
        Console.WriteLine();
        continue;
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("✓ Token acquired");
    Console.ResetColor();
    Console.WriteLine();

    ChatClientAgent agent;
    try
    {
        agent = CreateClientAgent(client, token, settings, http);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✓ Agent ready: {client.Name} Agent ({client.DeploymentId})");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ✗ Agent setup failed: {Truncate(ex.Message, 200)}");
        Console.ResetColor();
        Console.WriteLine();
        continue;
    }

    Console.WriteLine();

    int requestNum = 0;
    foreach (var prompt in settings.Prompts)
    {
        requestNum++;
        var displayPrompt = prompt.Length > 50 ? prompt[..50] + "…" : prompt;
        Console.Write($"  [{requestNum}/{settings.Prompts.Count}] \"{displayPrompt}\" ");

        try
        {
            var response = await agent.RunAsync(prompt, session: null, options: null, cancellationToken: CancellationToken.None);
            var promptTokens = response.Usage?.InputTokenCount ?? 0;
            var completionTokens = response.Usage?.OutputTokenCount ?? 0;
            var totalTokens = response.Usage?.TotalTokenCount ?? (promptTokens + completionTokens);
            var preview = string.IsNullOrWhiteSpace(response.Text) ? "no text response" : response.Text.Trim();
            if (preview.Length > 80)
                preview = preview[..80] + "…";

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ {totalTokens} tokens (prompt: {promptTokens}, completion: {completionTokens})");
            Console.ResetColor();
            Console.WriteLine($"         {preview}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("✗ 429 TooManyRequests");
            Console.WriteLine("         ⚠ Quota exceeded — this client is blocked");
            Console.ResetColor();
        }
        catch (HttpRequestException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ {(int?)ex.StatusCode ?? 0} {Truncate(ex.Message, 200)}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ Agent error: {Truncate(ex.Message, 200)}");
            Console.ResetColor();
        }

        await Task.Delay(500);
    }

    Console.WriteLine();
    Console.Write("  Fetching AI Policy summary... ");
    await PrintChargebackSummaryAsync(http, settings.AIPolicyBase, client.AppId);
    Console.WriteLine();
}

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("Demo complete! Check the dashboard at:");
Console.WriteLine($"  {settings.AIPolicyBase}/");
Console.ResetColor();

static void LoadEnvironmentFiles()
{
    var cwd = Directory.GetCurrentDirectory();
    var envFileCandidates = new[]
    {
        Path.Combine(cwd, ".env.local"),
        Path.Combine(cwd, ".env"),
        Path.Combine(cwd, "DemoClient", ".env.local"),
        Path.Combine(cwd, "DemoClient", ".env"),
        Path.Combine(cwd, "demo", ".env.local"),
        Path.Combine(cwd, "demo", ".env")
    };

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var candidate in envFileCandidates)
    {
        var fullPath = Path.GetFullPath(candidate);
        if (!File.Exists(fullPath) || !seen.Add(fullPath))
            continue;

        Env.Load(fullPath);
    }
}

static ChatClientAgent CreateClientAgent(
    DemoClientConfig client,
    string accessToken,
    DemoClientSettings settings,
    HttpClient http)
{
    var endpoint = new Uri($"{settings.ApimBase.TrimEnd('/')}/openai/deployments/{client.DeploymentId}/chat/completions?api-version={settings.ApiVersion}");
    var chatClient = new ApimChatClient(http, endpoint, accessToken);

    return new ChatClientAgent(
        chatClient,
        instructions: settings.AgentInstructions,
        name: $"{client.Name} Agent",
        description: "Agent Framework demo agent generating APIM usage traffic for chargeback validation.",
        tools: [],
        loggerFactory: null,
        services: null);
}

static async Task<string?> AcquireAccessTokenAsync(
    DemoClientConfig client,
    string tenantId,
    string apiScope)
{
    try
    {
        if (client.UseInteractiveAuth)
        {
            // Cross-tenant interactive auth: the API and client service principals
            // must already exist in the target tenant. The setup script or an admin
            // provisions them via: az ad sp create --id {appId} (run in the target tenant).
            var apiAppId = apiScope.Replace("api://", "").Replace("/.default", "");

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine();
            Console.WriteLine($"    ℹ Cross-tenant auth for tenant {tenantId}.");
            Console.WriteLine($"    Ensure service principals exist in the target tenant:");
            Console.WriteLine($"      az ad sp create --id {apiAppId}   # API app");
            Console.WriteLine($"      az ad sp create --id {client.AppId}   # Client app");
            Console.ResetColor();

            var publicApp = PublicClientApplicationBuilder
                .Create(client.AppId)
                .WithAuthority($"https://login.microsoftonline.com/organizations")
                .WithRedirectUri("http://localhost:29783")
                .Build();

            var authResult = await publicApp
                .AcquireTokenInteractive([apiScope.Replace("/.default", "/access_as_user")])
                .WithPrompt(Microsoft.Identity.Client.Prompt.Consent)
                .ExecuteAsync();

            return authResult.AccessToken;
        }
        else
        {
            // Client credentials flow for same-tenant service-to-service auth
            var msalApp = ConfidentialClientApplicationBuilder
                .Create(client.AppId)
                .WithClientSecret(client.Secret)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .Build();

            var authResult = await msalApp
                .AcquireTokenForClient([apiScope])
                .ExecuteAsync();

            return authResult.AccessToken;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ {ex.Message}");
        Console.ResetColor();
        return null;
    }
}

static async Task PrintChargebackSummaryAsync(
    HttpClient http,
    string aiPolicyEngineBase,
    string clientAppId)
{
    try
    {
        var cbResponse = await http.GetStringAsync($"{aiPolicyEngineBase}/chargeback");
        using var cbDoc = JsonDocument.Parse(cbResponse);

        if (!cbDoc.RootElement.TryGetProperty("logs", out var logs))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("✓ (no 'logs' property in response)");
            Console.ResetColor();
            return;
        }

        long totalTokensUsed = 0;
        decimal totalCostToUs = 0;
        decimal totalCostToCustomer = 0;
        int entryCount = 0;

        foreach (var log in logs.EnumerateArray())
        {
            if (!log.TryGetProperty("clientAppId", out var appIdProp) ||
                appIdProp.GetString() != clientAppId)
                continue;

            entryCount++;
            if (log.TryGetProperty("totalTokens", out var tt) && tt.TryGetInt64(out var parsedTokens))
                totalTokensUsed += parsedTokens;
            if (log.TryGetProperty("costToUs", out var cu))
                totalCostToUs += ParseMoney(cu.GetString());
            if (log.TryGetProperty("costToCustomer", out var cc))
                totalCostToCustomer += ParseMoney(cc.GetString());
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("✓");
        Console.WriteLine($"         Client entries:      {entryCount}");
        Console.WriteLine($"         Tokens used:         {totalTokensUsed:N0}");
        Console.WriteLine($"         Cost to us:          ${totalCostToUs:F4}");
        Console.WriteLine($"         Cost to customer:    ${totalCostToCustomer:F4}");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ {Truncate(ex.Message, 200)}");
        Console.ResetColor();
    }
}

static decimal ParseMoney(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return 0m;

    var cleaned = value.Replace("$", string.Empty, StringComparison.Ordinal).Trim();
    return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
}

static string Truncate(string value, int maxLength)
{
    if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        return value;

    return value[..maxLength] + "…";
}

file sealed class ApimChatClient(HttpClient http, Uri endpoint, string bearerToken) : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            messages = messages.Select(m => new
            {
                role = ToOpenAiRole(m.Role),
                content = m.Text ?? string.Empty
            }),
            max_completion_tokens = 60
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Service request failed. Status: {(int)response.StatusCode} ({response.StatusCode})",
                null,
                response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var text = ExtractAssistantText(root);
        var usage = ExtractUsage(root);

        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = usage,
            RawRepresentation = body
        };

        if (root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
            chatResponse.ModelId = modelProp.GetString();

        return chatResponse;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => null;

    public void Dispose()
    {
    }

    private static UsageDetails ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement))
            return new UsageDetails();

        var promptTokens = GetLong(usageElement, "prompt_tokens");
        var completionTokens = GetLong(usageElement, "completion_tokens");
        var totalTokens = GetLong(usageElement, "total_tokens");

        return new UsageDetails
        {
            InputTokenCount = promptTokens,
            OutputTokenCount = completionTokens,
            TotalTokenCount = totalTokens == 0 ? promptTokens + completionTokens : totalTokens
        };
    }

    private static long GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
            return value;

        return 0;
    }

    private static string ExtractAssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return string.Empty;

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message))
            return string.Empty;

        if (!message.TryGetProperty("content", out var content))
            return string.Empty;

        return content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : content.ToString();
    }

    private static string ToOpenAiRole(ChatRole role)
    {
        if (role == ChatRole.System) return "system";
        if (role == ChatRole.Assistant) return "assistant";
        if (role == ChatRole.Tool) return "tool";
        return "user";
    }
}

file sealed class DemoClientSettings
{
    public required string TenantId { get; init; }
    public string? SecondaryTenantId { get; init; }
    public required string ApiScope { get; init; }
    public required string ApimBase { get; init; }
    public required string ApiVersion { get; init; }
    public required string AIPolicyBase { get; init; }
    public required IReadOnlyList<DemoClientConfig> Clients { get; init; }
    public required IReadOnlyList<string> Prompts { get; init; }
    public required string AgentInstructions { get; init; }

    public static DemoClientSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("DemoClient");
        if (!section.Exists())
            throw new InvalidOperationException("Missing 'DemoClient' configuration section. Configure with user secrets or environment variables (DemoClient__*).");

        var defaultTenantId = ReadRequired(section, "TenantId");
        var secondaryTenantId = section["SecondaryTenantId"];

        var clientsSection = section.GetSection("Clients");
        var clients = clientsSection.GetChildren()
            .Select(c => ParseClientConfig(c, defaultTenantId))
            .ToList();
        if (clients.Count == 0)
            throw new InvalidOperationException("Missing DemoClient clients configuration. Add DemoClient:Clients entries.");

        // If a secondary tenant is configured, duplicate multi-tenant clients (all
        // clients after the first) so the demo generates traffic from both tenants.
        if (!string.IsNullOrWhiteSpace(secondaryTenantId))
        {
            var secondaryClients = clients
                .Skip(1) // First client is single-tenant; remaining are multi-tenant candidates
                .Select(c => c with
                {
                    Name = $"{c.Name} (Tenant 2)",
                    TenantId = secondaryTenantId,
                    UseInteractiveAuth = true
                })
                .ToList();
            clients.AddRange(secondaryClients);
        }

        var prompts = section.GetSection("Prompts")
            .GetChildren()
            .Select(p => p.Value?.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToArray();

        if (prompts.Length == 0)
            prompts = DefaultPrompts;

        var instructions = section["AgentInstructions"];
        if (string.IsNullOrWhiteSpace(instructions))
            instructions = DefaultAgentInstructions;

        return new DemoClientSettings
        {
            TenantId = defaultTenantId,
            SecondaryTenantId = secondaryTenantId,
            ApiScope = ReadRequired(section, "ApiScope"),
            ApimBase = ReadRequired(section, "ApimBase"),
            ApiVersion = ReadRequired(section, "ApiVersion"),
            AIPolicyBase = ReadRequired(section, "AIPolicyBase"),
            Clients = clients,
            Prompts = prompts,
            AgentInstructions = instructions
        };
    }

    private static DemoClientConfig ParseClientConfig(IConfigurationSection section, string defaultTenantId)
        => new(
            Name: ReadRequired(section, "Name"),
            AppId: ReadRequired(section, "AppId"),
            Secret: ReadRequired(section, "Secret"),
            Plan: ReadRequired(section, "Plan"),
            DeploymentId: ReadRequired(section, "DeploymentId"),
            TenantId: section["TenantId"] ?? defaultTenantId);

    private static string ReadRequired(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException($"Missing required DemoClient configuration key: {section.Path}:{key}");
    }

    private static readonly string[] DefaultPrompts =
    [
        "What is Azure API Management in one sentence?",
        "Explain token-based billing in one sentence.",
        "What is a rate limit?",
        "What is Microsoft Entra ID in one sentence?",
        "Summarize cloud cost management in one sentence.",
        "What is Azure OpenAI Service?",
        "Explain API gateway patterns in one sentence.",
        "What is a subscription key?",
        "Define chargeback in cloud computing.",
        "What is consumption-based pricing?"
    ];

    private const string DefaultAgentInstructions = "You are a concise Azure platform assistant. Keep responses to one sentence.";
}

file sealed record DemoClientConfig(
    string Name,
    string AppId,
    string Secret,
    string Plan,
    string DeploymentId,
    string TenantId,
    bool UseInteractiveAuth = false);
