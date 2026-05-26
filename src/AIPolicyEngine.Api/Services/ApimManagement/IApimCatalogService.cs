using AIPolicyEngine.Api.Models.Apim;

namespace AIPolicyEngine.Api.Services.ApimManagement;

public interface IApimCatalogService
{
    Task<IReadOnlyList<ApimApiSummary>> ListApisAsync(CancellationToken ct = default);
    Task<ApimApiSummary?> GetApiAsync(string apiId, CancellationToken ct = default);
    Task<IReadOnlyList<ApimOperationSummary>> ListOperationsAsync(string apiId, CancellationToken ct = default);
    Task<ApimOperationSummary?> GetOperationAsync(string apiId, string operationId, CancellationToken ct = default);
    Task<string?> GetApiPolicyXmlAsync(string apiId, CancellationToken ct = default);
    Task<string?> GetOperationPolicyXmlAsync(string apiId, string operationId, CancellationToken ct = default);
    Task ApplyApiPolicyAsync(string apiId, string xml, CancellationToken ct = default);
    Task ApplyOperationPolicyAsync(string apiId, string operationId, string xml, CancellationToken ct = default);
    Task SetApiPassthroughPolicyAsync(string apiId, CancellationToken ct = default);
    Task SetOperationPassthroughPolicyAsync(string apiId, string operationId, CancellationToken ct = default);
}
