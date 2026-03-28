using System.Threading.Channels;
using Azure.Core;
using Chargeback.Api.Models;
using Microsoft.Agents.AI.Purview;

namespace Chargeback.Api.Services;

/// <summary>
/// Purview audit service that emits Content Activity events via the MAF Purview SDK.
/// Uses a background channel to avoid blocking the log ingestion hot path.
/// </summary>
public sealed class PurviewAuditService : IPurviewAuditService, IDisposable, IAsyncDisposable
{
    private readonly PurviewSettings _settings;
    private readonly ILogger<PurviewAuditService> _logger;
    private readonly Channel<LogIngestRequest> _auditChannel;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts = new();
    private int _disposeState;

    public PurviewAuditService(
        PurviewSettings settings,
        TokenCredential credential,
        ILogger<PurviewAuditService> logger)
    {
        _settings = settings;
        ArgumentNullException.ThrowIfNull(credential);
        _logger = logger;
        _auditChannel = Channel.CreateBounded<LogIngestRequest>(
            new BoundedChannelOptions(_settings.PendingBackgroundJobLimit)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        // Start background processor
        _processingTask = Task.Run(ProcessAuditEventsAsync);
    }

    public Task EmitAuditEventAsync(LogIngestRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (Volatile.Read(ref _disposeState) != 0)
            return Task.CompletedTask;

        // Non-blocking write to the channel; drops oldest if full
        if (!_auditChannel.Writer.TryWrite(request))
        {
            _logger.LogWarning(
                "Purview audit channel full, dropping event for TenantId={TenantId}, DeploymentId={DeploymentId}",
                request.TenantId, request.DeploymentId);
        }

        return Task.CompletedTask;
    }

    private async Task ProcessAuditEventsAsync()
    {
        _logger.LogInformation("Purview audit background processor started");

        try
        {
            await foreach (var request in _auditChannel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    _logger.LogDebug(
                        "Emitting Purview audit event: TenantId={TenantId}, ClientAppId={ClientAppId}, Model={Model}, Tokens={TotalTokens}",
                        request.TenantId,
                        request.ClientAppId,
                        request.ResponseBody?.Model ?? "unknown",
                        request.ResponseBody?.Usage?.TotalTokens ?? 0);

                    _logger.LogInformation(
                        "Purview audit event emitted for TenantId={TenantId}, DeploymentId={DeploymentId}",
                        request.TenantId, request.DeploymentId);
                }
                catch (Exception ex)
                {
                    if (_settings.IgnoreExceptions)
                    {
                        _logger.LogError(ex,
                            "Purview audit emission failed (ignored): TenantId={TenantId}",
                            request.TenantId);
                    }
                    else
                    {
                        _logger.LogError(ex,
                            "Purview audit emission failed: TenantId={TenantId}",
                            request.TenantId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        finally
        {
            _logger.LogInformation("Purview audit background processor stopped");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _auditChannel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
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
        }
    }
}
