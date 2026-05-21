using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AIPolicyEngine.Api.Models.Apim;

namespace AIPolicyEngine.Api.Services.ApimManagement;

public sealed class ApimPolicyApplyService : IApimPolicyApplyService
{
    private readonly IApimCatalogService _catalogService;
    private readonly ITemplateLibraryService _templateLibraryService;
    private readonly IPolicyAssignmentRepository _assignmentRepository;
    private readonly Channel<ApimPolicyApplyWorkItem> _queue;
    private readonly ILogger<ApimPolicyApplyService> _logger;

    public ApimPolicyApplyService(
        IApimCatalogService catalogService,
        ITemplateLibraryService templateLibraryService,
        IPolicyAssignmentRepository assignmentRepository,
        Channel<ApimPolicyApplyWorkItem> queue,
        ILogger<ApimPolicyApplyService> logger)
    {
        _catalogService = catalogService;
        _templateLibraryService = templateLibraryService;
        _assignmentRepository = assignmentRepository;
        _queue = queue;
        _logger = logger;
    }

    public Task<ApplyPolicyAcceptedResponse> QueueApiPolicyApplyAsync(
        string apiId,
        ApplyPolicyRequest request,
        string appliedBy,
        CancellationToken ct = default)
        => QueueAssignmentAsync(apiId, null, request, appliedBy, ct);

    public Task<ApplyPolicyAcceptedResponse> QueueOperationPolicyApplyAsync(
        string apiId,
        string operationId,
        ApplyPolicyRequest request,
        string appliedBy,
        CancellationToken ct = default)
        => QueueAssignmentAsync(apiId, operationId, request, appliedBy, ct);

    public async Task ClearApiPolicyAsync(string apiId, CancellationToken ct = default)
    {
        _ = await _catalogService.GetApiAsync(apiId, ct)
            ?? throw new KeyNotFoundException($"APIM API '{apiId}' was not found.");

        await _catalogService.SetApiPassthroughPolicyAsync(apiId, ct);
        await _assignmentRepository.DeleteAsync(apiId, null, ct);

        _logger.LogInformation("Cleared APIM policy assignment for ApiId={ApiId}", apiId);
    }

    public async Task ClearOperationPolicyAsync(string apiId, string operationId, CancellationToken ct = default)
    {
        _ = await _catalogService.GetOperationAsync(apiId, operationId, ct)
            ?? throw new KeyNotFoundException($"APIM operation '{operationId}' was not found on API '{apiId}'.");

        await _catalogService.SetOperationPassthroughPolicyAsync(apiId, operationId, ct);
        await _assignmentRepository.DeleteAsync(apiId, operationId, ct);

        _logger.LogInformation("Cleared APIM policy assignment for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
    }

    public async Task<List<ApimPolicyApplyWorkItem>> ListRecoverableWorkItemsAsync(CancellationToken ct = default)
    {
        var assignments = await _assignmentRepository.GetAllAsync(ct);
        return assignments
            .Where(assignment => assignment.Status is PolicyAssignmentStatuses.Pending or PolicyAssignmentStatuses.Applying)
            .OrderBy(assignment => assignment.UpdatedAt)
            .Select(assignment => new ApimPolicyApplyWorkItem(assignment.ApiId, assignment.OperationId))
            .ToList();
    }

    public async Task ProcessAssignmentAsync(string apiId, string? operationId, CancellationToken ct = default)
    {
        var assignment = await _assignmentRepository.GetAsync(apiId, operationId, ct);
        if (assignment is null)
        {
            _logger.LogDebug("Skipping APIM policy apply because assignment no longer exists for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
            return;
        }

        assignment.Status = PolicyAssignmentStatuses.Applying;
        assignment.ErrorMessage = null;
        assignment.UpdatedAt = DateTime.UtcNow;
        await _assignmentRepository.UpsertAsync(assignment, ct);

        try
        {
            var renderedTemplate = await _templateLibraryService.RenderAsync(assignment.TemplateId, assignment.Parameters, ct);

            if (string.IsNullOrWhiteSpace(operationId))
            {
                await _catalogService.ApplyApiPolicyAsync(apiId, renderedTemplate.Xml, ct);
            }
            else
            {
                await _catalogService.ApplyOperationPolicyAsync(apiId, operationId, renderedTemplate.Xml, ct);
            }

            assignment.TemplateVersion = renderedTemplate.Manifest.Version;
            assignment.Parameters = renderedTemplate.Parameters;
            assignment.GeneratedXmlHash = ComputeSha256(renderedTemplate.Xml);
            assignment.LastAppliedAt = DateTime.UtcNow;
            assignment.Status = PolicyAssignmentStatuses.Synced;
            assignment.ErrorMessage = null;
            assignment.UpdatedAt = assignment.LastAppliedAt.Value;

            await _assignmentRepository.UpsertAsync(assignment, ct);

            _logger.LogInformation(
                "Applied APIM policy assignment for ApiId={ApiId} OperationId={OperationId} TemplateId={TemplateId}",
                apiId,
                operationId,
                assignment.TemplateId);
        }
        catch (Exception ex)
        {
            assignment.Status = PolicyAssignmentStatuses.Failed;
            assignment.ErrorMessage = ex.Message;
            assignment.GeneratedXmlHash = null;
            assignment.UpdatedAt = DateTime.UtcNow;
            await TryPersistFailureAsync(assignment, ct);

            _logger.LogError(
                ex,
                "Failed to apply APIM policy assignment for ApiId={ApiId} OperationId={OperationId} TemplateId={TemplateId}",
                apiId,
                operationId,
                assignment.TemplateId);
        }
    }

    private async Task<ApplyPolicyAcceptedResponse> QueueAssignmentAsync(
        string apiId,
        string? operationId,
        ApplyPolicyRequest request,
        string appliedBy,
        CancellationToken ct)
    {
        if (request is null)
        {
            throw new TemplateValidationException("Request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.TemplateId))
        {
            throw new TemplateValidationException("templateId is required.");
        }

        var api = await _catalogService.GetApiAsync(apiId, ct)
            ?? throw new KeyNotFoundException($"APIM API '{apiId}' was not found.");

        if (!string.IsNullOrWhiteSpace(operationId))
        {
            _ = await _catalogService.GetOperationAsync(apiId, operationId, ct)
                ?? throw new KeyNotFoundException($"APIM operation '{operationId}' was not found on API '{apiId}'.");
        }

        var renderedTemplate = await _templateLibraryService.RenderAsync(request.TemplateId, request.Parameters, ct);
        var existingAssignment = await _assignmentRepository.GetAsync(apiId, operationId, ct);
        var now = DateTime.UtcNow;

        var assignment = existingAssignment ?? new PolicyAssignment
        {
            ApiId = apiId,
            OperationId = operationId,
            CreatedAt = now
        };

        assignment.ApiDisplayName = api.DisplayName;
        assignment.TemplateId = renderedTemplate.Manifest.Id;
        assignment.TemplateVersion = renderedTemplate.Manifest.Version;
        assignment.Parameters = renderedTemplate.Parameters;
        assignment.GeneratedXmlHash = null;
        assignment.LastAppliedAt = null;
        assignment.AppliedBy = appliedBy;
        assignment.Status = PolicyAssignmentStatuses.Pending;
        assignment.ErrorMessage = null;
        assignment.UpdatedAt = now;

        await _assignmentRepository.UpsertAsync(assignment, ct);

        if (!_queue.Writer.TryWrite(new ApimPolicyApplyWorkItem(apiId, operationId)))
        {
            throw new InvalidOperationException("Failed to enqueue the APIM policy apply request.");
        }

        _logger.LogInformation(
            "Queued APIM policy assignment for ApiId={ApiId} OperationId={OperationId} TemplateId={TemplateId}",
            apiId,
            operationId,
            assignment.TemplateId);

        return new ApplyPolicyAcceptedResponse
        {
            AssignmentId = assignment.Id,
            Status = PolicyAssignmentStatuses.Applying
        };
    }

    private async Task TryPersistFailureAsync(PolicyAssignment assignment, CancellationToken ct)
    {
        try
        {
            await _assignmentRepository.UpsertAsync(assignment, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to persist APIM policy assignment failure state for ApiId={ApiId} OperationId={OperationId}",
                assignment.ApiId,
                assignment.OperationId);
        }
    }

    private static string ComputeSha256(string xml)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(xml));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
