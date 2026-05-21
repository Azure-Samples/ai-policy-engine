using AIPolicyEngine.Api.Models.Apim;

namespace AIPolicyEngine.Api.Services.ApimManagement;

public interface IPolicyAssignmentRepository
{
    Task<PolicyAssignment?> GetAsync(string apiId, string? operationId, CancellationToken ct = default);
    Task<List<PolicyAssignment>> GetAllAsync(CancellationToken ct = default);
    Task<PolicyAssignment> UpsertAsync(PolicyAssignment assignment, CancellationToken ct = default);
    Task<bool> DeleteAsync(string apiId, string? operationId, CancellationToken ct = default);
}
