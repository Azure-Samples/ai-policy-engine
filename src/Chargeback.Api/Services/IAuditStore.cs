using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Durable audit store backed by Cosmos DB for financial record-keeping and export.
/// </summary>
public interface IAuditStore
{
    /// <summary>
    /// Write a batch of audit log documents to Cosmos DB.
    /// </summary>
    Task WriteBatchAsync(IReadOnlyList<AuditLogDocument> documents, CancellationToken ct = default);

    /// <summary>
    /// Upsert billing summary documents from a batch of audit log items.
    /// Groups by customer (client+tenant)+deployment+period and increments totals.
    /// </summary>
    Task UpsertBillingSummariesAsync(IReadOnlyList<AuditLogItem> items, CancellationToken ct = default);

    /// <summary>
    /// Get all billing summaries for a given billing period (YYYY-MM).
    /// </summary>
    Task<List<BillingSummaryDocument>> GetBillingSummariesAsync(string billingPeriod, CancellationToken ct = default);

    /// <summary>
    /// Get all audit log entries for a specific customer in a billing period.
    /// </summary>
    Task<List<AuditLogDocument>> GetClientAuditLogsAsync(string customerKey, string billingPeriod, CancellationToken ct = default);

    /// <summary>
    /// Get all distinct billing periods that have data.
    /// </summary>
    Task<List<ExportPeriod>> GetAvailablePeriodsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get all clients that have data in a given billing period.
    /// </summary>
    Task<List<ExportClient>> GetClientsForPeriodAsync(string billingPeriod, CancellationToken ct = default);
}
