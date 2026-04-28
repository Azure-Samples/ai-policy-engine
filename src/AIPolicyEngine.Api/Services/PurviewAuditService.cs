using System.Text.Json;
using System.Threading.Channels;
using AIPolicyEngine.Api.Models;
using Azure.Core;
using Microsoft.Agents.AI.Purview;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Purview audit service that emits Content Activity events and evaluates DLP policies
/// by calling the Microsoft Graph REST API directly via <see cref="PurviewGraphClient"/>.
/// Uses a bounded background channel to avoid blocking the log-ingestion hot path.
/// </summary>
/// <remarks>
/// <para>
/// This service builds its own <see cref="PurviewGraphClient"/> that calls the same Graph
/// REST endpoints that the <c>internal sealed PurviewClient</c> inside
/// <c>Microsoft.Agents.AI.Purview</c> calls. We do this because rc6 of that SDK only
/// exposes <see cref="PurviewSettings"/>, <see cref="PurviewAppLocation"/>,
/// <see cref="PurviewLocationType"/>, and exception types publicly — the content-processing
/// pipeline (<c>PurviewClient</c>, <c>IScopedContentProcessor</c>,
/// <c>ScopedContentProcessor</c>, <c>IBackgroundJobRunner</c>) is <c>internal sealed</c>
/// and unreachable from outside the assembly.
/// </para>
/// <para>
/// Migration path: once <c>Microsoft.Agents.AI.Purview</c> promotes
/// <c>IScopedContentProcessor</c> / <c>ScopedContentProcessor</c> to its public surface,
/// <see cref="PurviewGraphClient"/> can be replaced with the SDK wrapper from
/// <c>PurviewExtensions.CreateWrapper()</c>.
/// </para>
/// </remarks>
public sealed class PurviewAuditService : IPurviewAuditService, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Internal wrapper queued on the background channel. Carries the per-request display name
    /// so the background processor can construct per-event <see cref="PurviewSettings"/>.
    /// </summary>
    private sealed record PurviewAuditEvent(LogIngestRequest Request, string ClientDisplayName);

    // ── Configuration ─────────────────────────────────────────────────────────────────────────
    private readonly PurviewSettings _baseSettings;
    private readonly bool _blockEnabled;
    private readonly ILogger<PurviewAuditService> _logger;

    // ── Graph client dependencies ──────────────────────────────────────────────────────────────
    private readonly TokenCredential _credential;
    private readonly HttpClient _graphHttpClient;
    private readonly bool _ownsGraphHttpClient;

    // ── Channel & lifecycle ────────────────────────────────────────────────────────────────────
    private readonly Channel<PurviewAuditEvent> _auditChannel;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts = new();
    private int _disposeState;

    // ── Retry constants ────────────────────────────────────────────────────────────────────────
    private const int RetryMaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    ];

    public PurviewAuditService(
        PurviewSettings baseSettings,
        TokenCredential credential,
        ILogger<PurviewAuditService> logger,
        bool blockEnabled = false,
        IHttpClientFactory? httpClientFactory = null)
    {
        _baseSettings = baseSettings;
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        _logger = logger;
        _blockEnabled = blockEnabled;

        if (httpClientFactory is not null)
        {
            _graphHttpClient = httpClientFactory.CreateClient("PurviewGraphClient");
            _ownsGraphHttpClient = false;
        }
        else
        {
            // Test / no-factory path — create a plain HttpClient (calls will fail gracefully).
            _graphHttpClient = new HttpClient();
            _ownsGraphHttpClient = true;
        }

        _auditChannel = Channel.CreateBounded<PurviewAuditEvent>(
            new BoundedChannelOptions(_baseSettings.PendingBackgroundJobLimit)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = true,
            });

        _processingTask = Task.Run(ProcessAuditEventsAsync);
    }

    // ── Hot path ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task EmitAuditEventAsync(
        LogIngestRequest request,
        string clientDisplayName,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (Volatile.Read(ref _disposeState) != 0)
            return Task.CompletedTask;

        var auditEvent = new PurviewAuditEvent(request, clientDisplayName);

        if (!_auditChannel.Writer.TryWrite(auditEvent))
        {
            _logger.LogWarning(
                "Purview audit channel full — dropping event for TenantId={TenantId} ClientAppId={ClientAppId}",
                request.TenantId,
                request.ClientAppId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<PurviewContentCheckResult> CheckContentAsync(
        string content,
        string tenantId,
        string clientDisplayName,
        CancellationToken cancellationToken = default)
    {
        if (!_blockEnabled)
            return new PurviewContentCheckResult { IsBlocked = false };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var settings = new PurviewSettings(clientDisplayName)
            {
                AppVersion = _baseSettings.AppVersion,
                TenantId = _baseSettings.TenantId,
                PurviewAppLocation = _baseSettings.PurviewAppLocation,
                IgnoreExceptions = _baseSettings.IgnoreExceptions,
                GraphBaseUri = _baseSettings.GraphBaseUri,
                BlockedPromptMessage = _baseSettings.BlockedPromptMessage,
                BlockedResponseMessage = _baseSettings.BlockedResponseMessage,
                InMemoryCacheSizeLimit = _baseSettings.InMemoryCacheSizeLimit,
                CacheTTL = _baseSettings.CacheTTL,
                PendingBackgroundJobLimit = _baseSettings.PendingBackgroundJobLimit,
                MaxConcurrentJobConsumers = _baseSettings.MaxConcurrentJobConsumers,
            };

            using var graphClient = new PurviewGraphClient(_credential, settings, _graphHttpClient, _logger);

            var tokenInfo = await graphClient.GetTokenInfoAsync(tenantId, cts.Token);
            var userId = tokenInfo.UserId;
            var effectiveTenantId = string.IsNullOrEmpty(tokenInfo.TenantId) ? tenantId : tokenInfo.TenantId;
            var correlationId = Guid.NewGuid().ToString("N");

            var scopesResult = await graphClient.GetProtectionScopesAsync(
                new PurviewProtectionScopesRequest
                {
                    UserId = userId,
                    TenantId = effectiveTenantId,
                    Activity = "UploadText",
                    CorrelationId = correlationId,
                }, cts.Token);

            if (scopesResult.ShouldProcess)
            {
                var contentRequest = new PurviewContentRequest
                {
                    UserId = userId,
                    TenantId = effectiveTenantId,
                    CorrelationId = correlationId,
                    MessageText = string.IsNullOrWhiteSpace(content) ? "(empty prompt)" : content,
                    MessageId = $"{correlationId}-precheck",
                    UserDisplayName = clientDisplayName,
                    Activity = "UploadText",
                };

                var processResult = await graphClient.ProcessContentAsync(
                    contentRequest, scopesResult.ScopeIdentifier, cts.Token);

                if (processResult.ShouldBlock)
                {
                    return new PurviewContentCheckResult
                    {
                        IsBlocked = true,
                        BlockMessage = settings.BlockedPromptMessage ?? "Content blocked by policy"
                    };
                }
            }

            return new PurviewContentCheckResult { IsBlocked = false };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Purview content check failed — failing open for TenantId={TenantId} ClientDisplayName={ClientDisplayName}",
                tenantId, clientDisplayName);
            return new PurviewContentCheckResult { IsBlocked = false };
        }
    }

    // ── Background processor ───────────────────────────────────────────────────────────────────

    private async Task ProcessAuditEventsAsync()
    {
        _logger.LogInformation("Purview audit background processor started");

        try
        {
            await foreach (var auditEvent in _auditChannel.Reader.ReadAllAsync(_cts.Token))
            {
                // Build per-event settings: override AppName with the client's display name
                // so Purview groups activity under the right application identity.
                var settings = new PurviewSettings(auditEvent.ClientDisplayName)
                {
                    AppVersion = _baseSettings.AppVersion,
                    TenantId = _baseSettings.TenantId,
                    PurviewAppLocation = _baseSettings.PurviewAppLocation,
                    IgnoreExceptions = _baseSettings.IgnoreExceptions,
                    GraphBaseUri = _baseSettings.GraphBaseUri,
                    BlockedPromptMessage = _baseSettings.BlockedPromptMessage,
                    BlockedResponseMessage = _baseSettings.BlockedResponseMessage,
                    InMemoryCacheSizeLimit = _baseSettings.InMemoryCacheSizeLimit,
                    CacheTTL = _baseSettings.CacheTTL,
                    PendingBackgroundJobLimit = _baseSettings.PendingBackgroundJobLimit,
                    MaxConcurrentJobConsumers = _baseSettings.MaxConcurrentJobConsumers,
                };

                await EmitWithRetryAsync(auditEvent, settings);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown — channel drained, processor exiting cleanly.
        }
        finally
        {
            _logger.LogInformation("Purview audit background processor stopped");
        }
    }

    private async Task EmitWithRetryAsync(PurviewAuditEvent auditEvent, PurviewSettings settings)
    {
        var request = auditEvent.Request;

        for (int attempt = 0; attempt <= RetryMaxAttempts; attempt++)
        {
            try
            {
                await EmitCoreAsync(auditEvent, settings);
                return;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // Service is shutting down — propagate so the outer loop exits.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Per-event cancellation (e.g. request-scoped token passed in). Log and drop.
                _logger.LogWarning(ex,
                    "Purview audit event cancelled (per-event) for TenantId={TenantId} ClientAppId={ClientAppId}",
                    request.TenantId, request.ClientAppId);
                return;
            }
            catch (PurviewRateLimitException ex) when (attempt < RetryMaxAttempts)
            {
                var delay = RetryDelays[attempt];
                _logger.LogWarning(ex,
                    "Purview rate limit hit (attempt {Attempt}/{Max}) — retrying in {Delay}s for TenantId={TenantId}",
                    attempt + 1, RetryMaxAttempts, delay.TotalSeconds, request.TenantId);
                await Task.Delay(delay, _cts.Token);
            }
            catch (PurviewRateLimitException ex)
            {
                LogEmissionFailure(ex, "rate limit (retries exhausted)", request, settings.IgnoreExceptions);
                return;
            }
            catch (PurviewAuthenticationException ex)
            {
                // DefaultAzureCredential refreshes tokens automatically; treat as transient.
                _logger.LogWarning(ex,
                    "Purview authentication error — event dropped for TenantId={TenantId} ClientAppId={ClientAppId}",
                    request.TenantId, request.ClientAppId);
                return;
            }
            catch (PurviewPaymentRequiredException ex)
            {
                // Licensing issue — log once at Warning, do not retry or spam Error.
                _logger.LogWarning(ex,
                    "Purview payment/licensing error — event dropped for TenantId={TenantId} ClientAppId={ClientAppId}",
                    request.TenantId, request.ClientAppId);
                return;
            }
            catch (PurviewRequestException ex)
            {
                LogEmissionFailure(ex, $"HTTP {ex.StatusCode}", request, settings.IgnoreExceptions);
                return;
            }
            catch (PurviewException ex)
            {
                // Catch-all for any other SDK-layer exception.
                LogEmissionFailure(ex, "SDK exception", request, settings.IgnoreExceptions);
                return;
            }
            catch (Exception ex)
            {
                // Unexpected — still silent-fail so we never crash the background loop.
                LogEmissionFailure(ex, "unexpected exception", request, settings.IgnoreExceptions);
                return;
            }
        }
    }

    /// <summary>
    /// Calls the Purview Graph REST API to emit audit events and (if enabled) evaluate DLP policy.
    /// Uses <see cref="PurviewGraphClient"/> — our own Graph REST implementation — because
    /// <c>IScopedContentProcessor</c> / <c>ScopedContentProcessor</c> are not yet public in
    /// <c>Microsoft.Agents.AI.Purview</c> rc6. See class-level remarks for the migration path.
    /// </summary>
    private async Task EmitCoreAsync(PurviewAuditEvent auditEvent, PurviewSettings settings)
    {
        var request = auditEvent.Request;

        _logger.LogDebug(
            "Emitting Purview audit event: TenantId={TenantId} ClientAppId={ClientAppId} " +
            "AppName={AppName} Model={Model} TotalTokens={TotalTokens}",
            request.TenantId,
            request.ClientAppId,
            settings.AppName,
            request.ResponseBody?.Model ?? "unknown",
            request.ResponseBody?.Usage?.TotalTokens ?? 0);

        // Construct a per-event graph client using the per-event PurviewSettings so that
        // AppName reflects the calling client's display name in the Graph audit records.
        using var graphClient = new PurviewGraphClient(_credential, settings, _graphHttpClient, _logger);

        // Resolve userId: acquire our own token in the request's tenant context and use the OID.
        // For service credentials (managed identity / client secret) the OID is the service
        // principal's object ID; for user-delegated tokens it is the user's OID.
        var tokenInfo = await graphClient.GetTokenInfoAsync(request.TenantId, _cts.Token);
        var userId = tokenInfo.UserId;
        var tenantId = string.IsNullOrEmpty(tokenInfo.TenantId) ? request.TenantId : tokenInfo.TenantId;
        var correlationId = request.CorrelationId ?? Guid.NewGuid().ToString("N");

        var promptText   = ExtractPromptText(request.RequestBody);
        var responseText = BuildResponseSummary(request.ResponseBody);

        var promptRequest = new PurviewContentRequest
        {
            UserId          = userId,
            TenantId        = tenantId,
            CorrelationId   = correlationId,
            MessageText     = string.IsNullOrWhiteSpace(promptText)   ? "(empty prompt)"   : promptText,
            MessageId       = $"{correlationId}-prompt",
            UserDisplayName = auditEvent.ClientDisplayName,
            Activity        = "UploadText",
        };

        var responseRequest = new PurviewContentRequest
        {
            UserId          = userId,
            TenantId        = tenantId,
            CorrelationId   = correlationId,
            MessageText     = string.IsNullOrWhiteSpace(responseText) ? "(empty response)" : responseText,
            MessageId       = $"{correlationId}-response",
            UserDisplayName = auditEvent.ClientDisplayName,
            Activity        = "DownloadText",
        };

        // Always emit the audit trail (fire-and-forget semantics within the background task).
        await graphClient.SendContentActivitiesAsync(promptRequest,   _cts.Token);
        await graphClient.SendContentActivitiesAsync(responseRequest, _cts.Token);

        // Optional inline DLP block evaluation.
        if (_blockEnabled)
        {
            bool shouldBlockPrompt   = false;
            bool shouldBlockResponse = false;

            var promptScopes = await graphClient.GetProtectionScopesAsync(
                new PurviewProtectionScopesRequest
                {
                    UserId        = userId,
                    TenantId      = tenantId,
                    Activity      = "UploadText",
                    CorrelationId = correlationId,
                }, _cts.Token);

            if (promptScopes.ShouldProcess)
            {
                var result = await graphClient.ProcessContentAsync(
                    promptRequest, promptScopes.ScopeIdentifier, _cts.Token);
                shouldBlockPrompt = result.ShouldBlock;
            }

            var responseScopes = await graphClient.GetProtectionScopesAsync(
                new PurviewProtectionScopesRequest
                {
                    UserId        = userId,
                    TenantId      = tenantId,
                    Activity      = "DownloadText",
                    CorrelationId = correlationId,
                }, _cts.Token);

            if (responseScopes.ShouldProcess)
            {
                var result = await graphClient.ProcessContentAsync(
                    responseRequest, responseScopes.ScopeIdentifier, _cts.Token);
                shouldBlockResponse = result.ShouldBlock;
            }

            if (shouldBlockPrompt || shouldBlockResponse)
            {
                _logger.LogWarning(
                    "PURVIEW_BLOCK_VERDICT: ClientAppId={ClientAppId} TenantId={TenantId} " +
                    "shouldBlockPrompt={ShouldBlockPrompt} shouldBlockResponse={ShouldBlockResponse}",
                    request.ClientAppId, request.TenantId, shouldBlockPrompt, shouldBlockResponse);
            }
        }

        _logger.LogInformation(
            "Purview audit event processed: TenantId={TenantId} ClientAppId={ClientAppId} DeploymentId={DeploymentId}",
            request.TenantId, request.ClientAppId, request.DeploymentId);
    }

    // ── Content extraction helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts human-readable prompt text from the raw request body.
    /// Handles Chat Completions (<c>messages[].content</c>) and Responses API (<c>input</c>).
    /// Falls back to the raw JSON when neither format is recognised.
    /// </summary>
    private static string ExtractPromptText(object? body)
    {
        if (body is null) return string.Empty;
        if (body is string s) return s;

        JsonElement el;
        if (body is JsonElement jsonEl)
        {
            el = jsonEl;
        }
        else
        {
            // Serialize and hand back the raw JSON for unknown object types.
            return JsonSerializer.Serialize(body);
        }

        // Chat Completions — extract messages[].content into a readable transcript.
        if (el.TryGetProperty("messages", out var messages) &&
            messages.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var msg in messages.EnumerateArray())
            {
                var role    = msg.TryGetProperty("role",    out var r) ? r.GetString() : null;
                var content = msg.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (!string.IsNullOrEmpty(content))
                    parts.Add($"[{role ?? "unknown"}] {content}");
            }
            if (parts.Count > 0)
                return string.Join("\n", parts);
        }

        // Responses API — single "input" string.
        if (el.TryGetProperty("input", out var input))
        {
            var inputStr = input.GetString();
            if (!string.IsNullOrEmpty(inputStr))
                return inputStr;
        }

        return el.GetRawText();
    }

    /// <summary>
    /// Builds a concise description of the AI response from the available metadata.
    /// The full response body is not forwarded by APIM, so only billing metadata is available.
    /// </summary>
    private static string BuildResponseSummary(OpenAiResponseBody? responseBody)
    {
        if (responseBody is null) return "(no response data)";
        var model  = responseBody.Model ?? "unknown";
        var tokens = responseBody.Usage?.TotalTokens ?? 0;
        return $"AI response from model '{model}' ({tokens} tokens)";
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private void LogEmissionFailure(Exception ex, string reason, LogIngestRequest request, bool ignoreExceptions)
    {
        // IgnoreExceptions controls whether a failure is noisy (Error) or quiet (Warning).
        // Both paths are silent-fail — we never let Purview errors surface to the caller.
        if (ignoreExceptions)
        {
            _logger.LogWarning(ex,
                "Purview audit emission failed ({Reason}) — ignored: TenantId={TenantId} ClientAppId={ClientAppId}",
                reason, request.TenantId, request.ClientAppId);
        }
        else
        {
            _logger.LogError(ex,
                "Purview audit emission failed ({Reason}): TenantId={TenantId} ClientAppId={ClientAppId}",
                reason, request.TenantId, request.ClientAppId);
        }
    }

    // ── Disposal ───────────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _auditChannel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
        if (_ownsGraphHttpClient) _graphHttpClient.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _auditChannel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        finally
        {
            _cts.Dispose();
            if (_ownsGraphHttpClient) _graphHttpClient.Dispose();
        }
    }
}
