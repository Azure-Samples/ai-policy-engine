using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// No-op implementation used when Purview is not configured.
/// </summary>
public sealed class NoOpPurviewAuditService : IPurviewAuditService
{
    public Task EmitAuditEventAsync(LogIngestRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
