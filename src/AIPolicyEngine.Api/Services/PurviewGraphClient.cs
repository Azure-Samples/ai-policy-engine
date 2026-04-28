using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Agents.AI.Purview;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Direct Graph REST API client for Microsoft Purview DLP evaluation and audit emission.
/// Implements the same endpoint calls as the <c>internal sealed PurviewClient</c> inside
/// <c>Microsoft.Agents.AI.Purview</c>, which is not yet accessible from outside the assembly.
/// </summary>
/// <remarks>
/// <para>
/// Migration path: once <c>Microsoft.Agents.AI.Purview</c> promotes
/// <c>IScopedContentProcessor</c> / <c>ScopedContentProcessor</c> to its public surface,
/// this class can be replaced with the SDK wrapper created by
/// <c>PurviewExtensions.CreateWrapper()</c>. Until then, every Graph REST call is made
/// directly here using the endpoint signatures observed in the SDK source.
/// </para>
/// <para>
/// The three Graph endpoints used are:
/// <list type="bullet">
///   <item><c>POST /users/{userId}/dataSecurityAndGovernance/protectionScopes/compute</c></item>
///   <item><c>POST /users/{userId}/dataSecurityAndGovernance/processContent</c></item>
///   <item><c>POST /{userId}/dataSecurityAndGovernance/activities/contentActivities</c></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class PurviewGraphClient : IDisposable
{
    private readonly TokenCredential _credential;
    private readonly PurviewSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string[] _scopes;
    private readonly JsonSerializerOptions _jsonOptions;

    private static readonly Uri DefaultGraphBaseUri = new("https://graph.microsoft.com/");
    private const string UserAgentHeader = "agent-framework-dotnet";

    public PurviewGraphClient(
        TokenCredential credential,
        PurviewSettings settings,
        HttpClient httpClient,
        ILogger logger)
    {
        _credential = credential;
        _settings = settings;
        _httpClient = httpClient;
        _logger = logger;

        var host = (_settings.GraphBaseUri ?? DefaultGraphBaseUri).Host;
        _scopes = [$"https://{host}/.default"];

        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    // ── Public API ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acquires a token for the given tenant and decodes its JWT claims to extract
    /// <c>userId</c>, <c>tenantId</c>, and <c>clientId</c>.
    /// </summary>
    public async Task<PurviewTokenInfo> GetTokenInfoAsync(string? tenantId, CancellationToken ct)
    {
        var (info, _) = await AcquireTokenAsync(tenantId, ct);
        return info;
    }

    /// <summary>
    /// Fire-and-forget audit trail — POST to the <c>contentActivities</c> endpoint.
    /// </summary>
    public async Task SendContentActivitiesAsync(PurviewContentRequest request, CancellationToken ct)
    {
        var (tokenInfo, rawToken) = await AcquireTokenAsync(request.TenantId, ct);
        var effectiveUserId = Coalesce(request.UserId, tokenInfo.UserId);
        var effectiveTenantId = Coalesce(request.TenantId, tokenInfo.TenantId);

        var graphUri = _settings.GraphBaseUri ?? DefaultGraphBaseUri;
        var url = new Uri(graphUri, $"{effectiveUserId}/dataSecurityAndGovernance/activities/contentActivities");

        var body = new GraphContentActivitiesRequest
        {
            UserId = effectiveUserId,
            TenantId = effectiveTenantId,
            ContentToProcess = BuildContentToProcess(request, tokenInfo),
            CorrelationId = request.CorrelationId,
        };

        using var httpRequest = BuildJsonRequest(HttpMethod.Post, url, rawToken, body);
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureSuccess(response, "contentActivities");
    }

    /// <summary>
    /// Inline DLP policy evaluation — POST to <c>processContent</c>.
    /// Returns a <see cref="PurviewProcessContentResult"/> with <c>ShouldBlock</c> set when
    /// any returned policy action is <c>BlockAccess</c> or <c>Block</c>.
    /// </summary>
    public async Task<PurviewProcessContentResult> ProcessContentAsync(
        PurviewContentRequest request,
        string? scopeIdentifier,
        CancellationToken ct)
    {
        var (tokenInfo, rawToken) = await AcquireTokenAsync(request.TenantId, ct);
        var effectiveUserId = Coalesce(request.UserId, tokenInfo.UserId);
        var effectiveTenantId = Coalesce(request.TenantId, tokenInfo.TenantId);

        var graphUri = _settings.GraphBaseUri ?? DefaultGraphBaseUri;
        var url = new Uri(graphUri, $"users/{effectiveUserId}/dataSecurityAndGovernance/processContent");

        var body = new GraphProcessContentRequest
        {
            UserId = effectiveUserId,
            TenantId = effectiveTenantId,
            ContentToProcess = BuildContentToProcess(request, tokenInfo),
            CorrelationId = request.CorrelationId,
        };

        using var httpRequest = BuildJsonRequest(HttpMethod.Post, url, rawToken, body);
        if (!string.IsNullOrEmpty(scopeIdentifier))
            httpRequest.Headers.TryAddWithoutValidation("If-None-Match", scopeIdentifier);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);
        EnsureSuccess(response, "processContent");

        var result = await response.Content.ReadFromJsonAsync<GraphProcessContentResponse>(_jsonOptions, ct);
        var actions = result?.PolicyActions ?? [];

        return new PurviewProcessContentResult
        {
            ShouldBlock = actions.Any(a =>
                a.Equals("BlockAccess", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("Block",       StringComparison.OrdinalIgnoreCase)),
            PolicyActions = actions,
        };
    }

    /// <summary>
    /// Asks the Graph API whether this tenant/user pair needs DLP processing at all.
    /// Returns <see cref="PurviewProtectionScopesResult.ShouldProcess"/> = false when
    /// the tenant has no applicable Purview DLP policy — allowing the caller to skip the
    /// more expensive <see cref="ProcessContentAsync"/> call.
    /// </summary>
    public async Task<PurviewProtectionScopesResult> GetProtectionScopesAsync(
        PurviewProtectionScopesRequest request,
        CancellationToken ct)
    {
        var (tokenInfo, rawToken) = await AcquireTokenAsync(request.TenantId, ct);
        var effectiveUserId = Coalesce(request.UserId, tokenInfo.UserId);
        var effectiveTenantId = Coalesce(request.TenantId, tokenInfo.TenantId);

        var graphUri = _settings.GraphBaseUri ?? DefaultGraphBaseUri;
        var url = new Uri(graphUri, $"users/{effectiveUserId}/dataSecurityAndGovernance/protectionScopes/compute");

        var body = new GraphProtectionScopesRequest
        {
            UserId = effectiveUserId,
            TenantId = effectiveTenantId,
            Activities = request.Activity,
            Locations = [BuildLocation(tokenInfo.ClientId)],
            DeviceMetadata = new GraphDeviceMetadata(),
            IntegratedAppMetadata = new GraphAppMetadata
            {
                Name    = _settings.AppName,
                Version = _settings.AppVersion ?? "1.0.0",
            },
            CorrelationId = request.CorrelationId,
        };

        using var httpRequest = BuildJsonRequest(HttpMethod.Post, url, rawToken, body);
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);
        EnsureSuccess(response, "protectionScopes");

        var result = await response.Content.ReadFromJsonAsync<GraphProtectionScopesResponse>(_jsonOptions, ct);
        return new PurviewProtectionScopesResult
        {
            ShouldProcess    = result?.ShouldProcess ?? false,
            ScopeIdentifier  = result?.ScopeIdentifier,
            ExecutionMode    = result?.ExecutionMode,
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acquires a token via <see cref="TokenCredential"/> and decodes its JWT payload.
    /// The raw token string is used as the bearer for subsequent HTTP requests.
    /// </summary>
    private async Task<(PurviewTokenInfo Info, string RawToken)> AcquireTokenAsync(
        string? tenantId,
        CancellationToken ct)
    {
        var context = new TokenRequestContext(_scopes, tenantId: tenantId);
        var token = await _credential.GetTokenAsync(context, ct);
        var info = DecodeJwtClaims(token.Token);
        return (info, token.Token);
    }

    private HttpRequestMessage BuildJsonRequest<T>(
        HttpMethod method,
        Uri url,
        string bearerToken,
        T body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearerToken}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgentHeader);
        request.Content = JsonContent.Create(body, options: _jsonOptions);
        return request;
    }

    private GraphContentToProcess BuildContentToProcess(
        PurviewContentRequest request,
        PurviewTokenInfo tokenInfo)
    {
        var location = BuildLocation(tokenInfo.ClientId);

        return new GraphContentToProcess
        {
            ConversationData =
            [
                new GraphConversationMetadata
                {
                    Content       = new GraphTextItem { Data = request.MessageText },
                    Identifier    = request.MessageId,
                    IsFinal       = false,
                    Name          = request.UserDisplayName,
                    CorrelationId = request.CorrelationId + "@AF",
                    SequenceNumber = DateTime.UtcNow.Ticks,
                }
            ],
            ActivityMetadata     = new GraphActivityMetadata { Activity = request.Activity },
            DeviceMetadata       = new GraphDeviceMetadata(),
            IntegratedAppMetadata = new GraphAppMetadata
            {
                Name    = _settings.AppName,
                Version = _settings.AppVersion ?? "1.0.0",
            },
            ProtectedAppMetadata = new GraphProtectedAppMetadata
            {
                ApplicationLocation = location,
                Name    = _settings.AppName,
                Version = _settings.AppVersion ?? "1.0.0",
            },
        };
    }

    /// <summary>
    /// Maps <see cref="PurviewSettings.PurviewAppLocation"/> to the Graph OData type/value pair.
    /// Falls back to <c>microsoft.graph.policyLocationApplication</c> with the service
    /// principal <c>clientId</c> when no explicit location is configured.
    /// </summary>
    private GraphLocation BuildLocation(string fallbackClientId)
    {
        if (_settings.PurviewAppLocation is { } appLocation)
        {
            return appLocation.LocationType switch
            {
                PurviewLocationType.Domain => new GraphLocation
                {
                    ODataType = "microsoft.graph.policyLocationDomain",
                    Value     = appLocation.LocationValue,
                },
                _ => new GraphLocation
                {
                    ODataType = "microsoft.graph.policyLocationApplication",
                    Value     = appLocation.LocationValue,
                },
            };
        }

        return new GraphLocation
        {
            ODataType = "microsoft.graph.policyLocationApplication",
            Value     = fallbackClientId,
        };
    }

    /// <summary>
    /// Throws the appropriate SDK-typed exception based on the HTTP status code, so the
    /// caller's existing catch ladder in <see cref="PurviewAuditService"/> continues to work.
    /// </summary>
    private static void EnsureSuccess(HttpResponseMessage response, string endpointName)
    {
        if (response.IsSuccessStatusCode) return;

        throw response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests =>
                new PurviewRateLimitException($"Rate limited at Purview endpoint '{endpointName}'"),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new PurviewAuthenticationException($"Authentication failed at Purview endpoint '{endpointName}'"),
            HttpStatusCode.PaymentRequired =>
                new PurviewPaymentRequiredException($"License/payment required at Purview endpoint '{endpointName}'"),
            _ =>
                new PurviewRequestException(response.StatusCode, endpointName),
        };
    }

    /// <summary>
    /// Decodes JWT claims from the base64url payload segment.
    /// Returns an empty <see cref="PurviewTokenInfo"/> if the token cannot be parsed.
    /// </summary>
    private static PurviewTokenInfo DecodeJwtClaims(string jwtToken)
    {
        var parts = jwtToken.Split('.');
        if (parts.Length < 2)
            return new PurviewTokenInfo();

        try
        {
            // Re-pad base64url → standard base64
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            var oid   = TryGetString(root, "oid");
            var tid   = TryGetString(root, "tid");
            var appid = TryGetString(root, "appid") ?? TryGetString(root, "azp");
            var idtyp = TryGetString(root, "idtyp");

            return new PurviewTokenInfo
            {
                UserId      = oid   ?? string.Empty,
                TenantId    = tid   ?? string.Empty,
                ClientId    = appid ?? string.Empty,
                IsUserToken = idtyp == "user",
            };
        }
        catch
        {
            return new PurviewTokenInfo();
        }
    }

    private static string? TryGetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) ? prop.GetString() : null;

    private static string Coalesce(string preferred, string fallback)
        => string.IsNullOrEmpty(preferred) ? fallback : preferred;

    // HttpClient is owned by the DI container via IHttpClientFactory — do not dispose here.
    public void Dispose() { }
}
