using AIPolicyEngine.Api.Models;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// No-op implementation used when Purview is not configured.
/// </summary>
public sealed class NoOpPurviewAuditService : IPurviewAuditService
{
    public Task EmitAuditEventAsync(LogIngestRequest request, string clientDisplayName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<PurviewContentCheckResult> CheckContentAsync(
        string content,
        string tenantId,
        string clientDisplayName,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new PurviewContentCheckResult { IsBlocked = false });
}
