using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ApiManagement;
using Azure.ResourceManager.ApiManagement.Models;
using AIPolicyEngine.Api.Models.Apim;
using Microsoft.Extensions.Options;

namespace AIPolicyEngine.Api.Services.ApimManagement;

public sealed class ApimCatalogService : IApimCatalogService
{
    private const string PassthroughPolicyXml = "<policies><inbound><base /></inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>";

    private readonly ArmClient _armClient;
    private readonly string _resourceIdText;
    private readonly ILogger<ApimCatalogService> _logger;
    private ApiManagementServiceResource? _service;

    public ApimCatalogService(
        ArmClient armClient,
        IOptions<ApimManagementOptions> options,
        ILogger<ApimCatalogService> logger)
    {
        ArgumentNullException.ThrowIfNull(armClient);
        ArgumentNullException.ThrowIfNull(options);

        _armClient = armClient;
        _resourceIdText = options.Value.ResourceId;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ApimApiSummary>> ListApisAsync(CancellationToken ct = default)
    {
        var service = GetService();
        var apis = new List<ApimApiSummary>();
        await foreach (var api in service.GetApis().GetAllAsync(cancellationToken: ct))
        {
            apis.Add(MapApi(api.Data));
        }

        return apis
            .OrderBy(api => api.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(api => api.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ApimApiSummary?> GetApiAsync(string apiId, CancellationToken ct = default)
    {
        var api = await GetApiResourceAsync(apiId, ct);
        return api is null ? null : MapApi(api.Data);
    }

    public async Task<IReadOnlyList<ApimOperationSummary>> ListOperationsAsync(string apiId, CancellationToken ct = default)
    {
        var api = await GetApiResourceOrThrowAsync(apiId, ct);
        var operations = new List<ApimOperationSummary>();

        await foreach (var operation in api.GetApiOperations().GetAllAsync(cancellationToken: ct))
        {
            operations.Add(MapOperation(operation.Data));
        }

        return operations
            .OrderBy(operation => operation.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(operation => operation.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ApimOperationSummary?> GetOperationAsync(string apiId, string operationId, CancellationToken ct = default)
    {
        var operation = await GetOperationResourceAsync(apiId, operationId, ct);
        return operation is null ? null : MapOperation(operation.Data);
    }

    public async Task<string?> GetApiPolicyXmlAsync(string apiId, CancellationToken ct = default)
    {
        var api = await GetApiResourceOrThrowAsync(apiId, ct);
        var policy = await api.GetApiPolicies().GetIfExistsAsync(PolicyName.Policy, PolicyExportFormat.RawXml, ct);
        return policy.HasValue ? policy.Value!.Data.Value : null;
    }

    public async Task<string?> GetOperationPolicyXmlAsync(string apiId, string operationId, CancellationToken ct = default)
    {
        var operation = await GetOperationResourceOrThrowAsync(apiId, operationId, ct);
        var policy = await operation.GetApiOperationPolicies().GetIfExistsAsync(PolicyName.Policy, PolicyExportFormat.RawXml, ct);
        return policy.HasValue ? policy.Value!.Data.Value : null;
    }

    public async Task ApplyApiPolicyAsync(string apiId, string xml, CancellationToken ct = default)
    {
        var api = await GetApiResourceOrThrowAsync(apiId, ct);
        await api.GetApiPolicies().CreateOrUpdateAsync(
            WaitUntil.Completed,
            PolicyName.Policy,
            CreatePolicyContract(xml),
            ifMatch: null,
            ct);

        _logger.LogInformation("Applied API-scoped APIM policy for ApiId={ApiId}", apiId);
    }

    public async Task ApplyOperationPolicyAsync(string apiId, string operationId, string xml, CancellationToken ct = default)
    {
        var operation = await GetOperationResourceOrThrowAsync(apiId, operationId, ct);
        await operation.GetApiOperationPolicies().CreateOrUpdateAsync(
            WaitUntil.Completed,
            PolicyName.Policy,
            CreatePolicyContract(xml),
            ifMatch: null,
            ct);

        _logger.LogInformation("Applied operation-scoped APIM policy for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
    }

    public Task SetApiPassthroughPolicyAsync(string apiId, CancellationToken ct = default)
        => ApplyApiPolicyAsync(apiId, PassthroughPolicyXml, ct);

    public Task SetOperationPassthroughPolicyAsync(string apiId, string operationId, CancellationToken ct = default)
        => ApplyOperationPolicyAsync(apiId, operationId, PassthroughPolicyXml, ct);

    private async Task<ApiResource?> GetApiResourceAsync(string apiId, CancellationToken ct)
    {
        var response = await GetService().GetApis().GetIfExistsAsync(apiId, ct);
        return response.HasValue ? response.Value : null;
    }

    private async Task<ApiResource> GetApiResourceOrThrowAsync(string apiId, CancellationToken ct)
        => await GetApiResourceAsync(apiId, ct)
           ?? throw new KeyNotFoundException($"APIM API '{apiId}' was not found.");

    private async Task<ApiOperationResource?> GetOperationResourceAsync(string apiId, string operationId, CancellationToken ct)
    {
        var api = await GetApiResourceAsync(apiId, ct);
        if (api is null)
        {
            return null;
        }

        var response = await api.GetApiOperations().GetIfExistsAsync(operationId, ct);
        return response.HasValue ? response.Value : null;
    }

    private async Task<ApiOperationResource> GetOperationResourceOrThrowAsync(string apiId, string operationId, CancellationToken ct)
        => await GetOperationResourceAsync(apiId, operationId, ct)
           ?? throw new KeyNotFoundException($"APIM operation '{operationId}' was not found on API '{apiId}'.");

    private ApiManagementServiceResource GetService()
    {
        if (_service is not null)
        {
            return _service;
        }

        if (string.IsNullOrWhiteSpace(_resourceIdText))
        {
            throw new InvalidOperationException("Apim:ResourceId must be configured before APIM management endpoints can be used.");
        }

        ResourceIdentifier resourceId;
        try
        {
            resourceId = new ResourceIdentifier(_resourceIdText);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Apim:ResourceId '{_resourceIdText}' is not a valid Azure resource ID.", ex);
        }

        if (!string.Equals(resourceId.ResourceType.ToString(), "Microsoft.ApiManagement/service", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Apim:ResourceId '{_resourceIdText}' must target a Microsoft.ApiManagement/service resource.");
        }

        _service = _armClient.GetApiManagementServiceResource(resourceId);
        return _service;
    }

    private static PolicyContractData CreatePolicyContract(string xml)
        => new()
        {
            Format = PolicyContentFormat.RawXml,
            Value = xml
        };

    private static ApimApiSummary MapApi(ApiData data)
        => new()
        {
            Id = data.Name,
            DisplayName = data.DisplayName ?? data.Name,
            Path = data.Path ?? string.Empty,
            ServiceUrl = data.ServiceUri?.ToString() ?? string.Empty,
            IsCurrent = data.IsCurrent ?? true
        };

    private static ApimOperationSummary MapOperation(ApiOperationData data)
        => new()
        {
            Id = data.Name,
            DisplayName = data.DisplayName ?? data.Name,
            Method = data.Method ?? string.Empty,
            UrlTemplate = data.UriTemplate ?? string.Empty
        };
}
