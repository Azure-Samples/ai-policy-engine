using AIPolicyEngine.Api.Models.Apim;

namespace AIPolicyEngine.Api.Services.ApimManagement;

public interface IApimPolicyApplyService
{
    Task<ApplyPolicyAcceptedResponse> QueueApiPolicyApplyAsync(string apiId, ApplyPolicyRequest request, string appliedBy, CancellationToken ct = default);
    Task<ApplyPolicyAcceptedResponse> QueueOperationPolicyApplyAsync(string apiId, string operationId, ApplyPolicyRequest request, string appliedBy, CancellationToken ct = default);
    Task ClearApiPolicyAsync(string apiId, CancellationToken ct = default);
    Task ClearOperationPolicyAsync(string apiId, string operationId, CancellationToken ct = default);
}
