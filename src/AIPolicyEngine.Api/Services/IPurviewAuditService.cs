using AIPolicyEngine.Api.Models;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Interface for emitting audit events to Microsoft Purview.
/// </summary>
public interface IPurviewAuditService
{
    /// <summary>
    /// Emits an audit event for an AI interaction log entry.
    /// This is fire-and-forget; failures are logged but do not block the request.
    /// </summary>
    /// <param name="request">The ingested log request containing prompt and response bodies.</param>
    /// <param name="clientDisplayName">
    /// Friendly display name of the client application (used as <c>AppName</c> in per-event
    /// <see cref="Microsoft.Agents.AI.Purview.PurviewSettings"/>). Falls back to
    /// <see cref="LogIngestRequest.ClientAppId"/> when no display name is available.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EmitAuditEventAsync(LogIngestRequest request, string clientDisplayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously evaluates whether the given content should be blocked by Purview DLP policy.
    /// Called at request time (precheck), before the LLM processes the prompt.
    /// Returns <see cref="PurviewContentCheckResult.IsBlocked"/> = false when blocking is disabled
    /// or Purview is unavailable — this call must never prevent the request from proceeding.
    /// </summary>
    /// <param name="content">The prompt text to evaluate.</param>
    /// <param name="tenantId">The tenant context for DLP policy lookup.</param>
    /// <param name="clientDisplayName">
    /// The client's Entra app display name — used as AppName in the Graph API payload
    /// so Purview ties the event to the right application identity.
    /// </param>
    /// <param name="cancellationToken">Cancellation token (honour it — the precheck hot path has tight timeouts).</param>
    Task<PurviewContentCheckResult> CheckContentAsync(
        string content,
        string tenantId,
        string clientDisplayName,
        CancellationToken cancellationToken = default);
}
