using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Background service that reads audit log items from an in-process channel
/// and writes them to Cosmos DB in batches for optimal throughput.
/// </summary>
public sealed class AuditLogWriter : BackgroundService
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    private const int MaxRetries = 3;

    private readonly ChannelReader<AuditLogItem> _channel;
    private readonly IAuditStore _auditStore;
    private readonly ILogger<AuditLogWriter> _logger;

    public AuditLogWriter(
        Channel<AuditLogItem> channel,
        IAuditStore auditStore,
        ILogger<AuditLogWriter> logger)
    {
        _channel = channel.Reader;
        _auditStore = auditStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AuditLogWriter started");

        var batch = new List<AuditLogItem>(MaxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                batch.Clear();

                // Wait for at least one item
                if (await _channel.WaitToReadAsync(stoppingToken))
                {
                    // Drain up to MaxBatchSize or until FlushInterval elapses
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(FlushInterval);

                    try
                    {
                        while (batch.Count < MaxBatchSize && _channel.TryRead(out var item))
                        {
                            batch.Add(item);
                        }

                        // If we got items immediately but haven't hit max, wait a bit for more
                        if (batch.Count < MaxBatchSize)
                        {
                            try
                            {
                                while (batch.Count < MaxBatchSize &&
                                       await _channel.WaitToReadAsync(cts.Token))
                                {
                                    while (batch.Count < MaxBatchSize && _channel.TryRead(out var item))
                                    {
                                        batch.Add(item);
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                            {
                                // FlushInterval elapsed — flush what we have
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // FlushInterval elapsed
                    }

                    if (batch.Count > 0)
                    {
                        await FlushBatchWithRetryAsync(batch, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in AuditLogWriter loop");
                // Avoid tight error loop
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        // Drain remaining items on shutdown
        batch.Clear();
        while (_channel.TryRead(out var remaining))
        {
            batch.Add(remaining);
        }
        if (batch.Count > 0)
        {
            _logger.LogInformation("Flushing {Count} remaining audit items on shutdown", batch.Count);
            await FlushBatchWithRetryAsync(batch, CancellationToken.None);
        }

        _logger.LogInformation("AuditLogWriter stopped");
    }

    private async Task FlushBatchWithRetryAsync(List<AuditLogItem> batch, CancellationToken ct)
    {
        // Build documents with deterministic IDs ONCE before the retry loop.
        // Deterministic IDs ensure retries are idempotent — partial success + retry
        // won't create duplicate billing records.
        var documents = batch.Select(item => new AuditLogDocument
        {
            Id = GenerateDeterministicId(item),
            CustomerKey = $"{item.ClientAppId}:{item.TenantId}",
            ClientAppId = item.ClientAppId,
            TenantId = item.TenantId,
            Audience = item.Audience,
            DeploymentId = item.DeploymentId,
            Model = item.Model,
            PromptTokens = item.PromptTokens,
            CompletionTokens = item.CompletionTokens,
            TotalTokens = item.TotalTokens,
            ImageTokens = item.ImageTokens,
            CostToUs = item.CostToUs,
            CostToCustomer = item.CostToCustomer,
            IsOverbilled = item.IsOverbilled,
            StatusCode = item.StatusCode,
            Timestamp = item.Timestamp,
            BillingPeriod = $"{item.Timestamp:yyyy-MM}",
            RequestedDeploymentId = item.RequestedDeploymentId,
            RoutingPolicyId = item.RoutingPolicyId,
            Multiplier = item.Multiplier,
            EffectiveRequestCost = item.EffectiveRequestCost,
            TierName = item.TierName
        }).ToList();

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await _auditStore.WriteBatchAsync(documents, ct);
                await _auditStore.UpsertBillingSummariesAsync(batch, ct);

                _logger.LogInformation("Flushed {Count} audit items to Cosmos DB", batch.Count);
                return;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex,
                    "Cosmos write failed (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt, MaxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Cosmos write failed after {Max} attempts, dropping {Count} items",
                    MaxRetries, batch.Count);
            }
        }
    }

    /// <summary>
    /// Generates a deterministic document ID from the audit item's identity fields.
    /// Same input always produces the same ID, making retries idempotent.
    /// </summary>
    private static string GenerateDeterministicId(AuditLogItem item)
    {
        var input = $"{item.ClientAppId}|{item.TenantId}|{item.DeploymentId}|{item.Timestamp:O}|{item.TotalTokens}|{item.PromptTokens}|{item.CorrelationId}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes)[..32].ToLowerInvariant();
    }
}
