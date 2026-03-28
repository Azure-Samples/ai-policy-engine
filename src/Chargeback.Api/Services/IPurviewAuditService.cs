using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Interface for emitting audit events to Microsoft Purview.
/// </summary>
public interface IPurviewAuditService
{
    /// <summary>
    /// Emits an audit event for an AI interaction log entry.
    /// This is fire-and-forget; failures are logged but do not block the request.
    /// </summary>
    Task EmitAuditEventAsync(LogIngestRequest request, CancellationToken cancellationToken = default);
}
