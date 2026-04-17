using Azure.Core;
using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Agent365 Observability service wrapper — creates InvokeAgent and Inference scopes.
/// Uses lightweight identity (ClientAppId as agent ID) without provisioned Agentic Users.
/// </summary>
/// <remarks>
/// NOTE: This is a minimal stub implementation for version 0.1.75-beta.
/// The SDK API surface is still evolving. Once the SDK stabilizes with documented
/// scope creation APIs (InvokeAgentScope.Start, InferenceScope.Start, etc.),
/// this service should be updated to use those patterns.
/// </remarks>
public interface IAgent365ObservabilityService
{
    IDisposable? StartInvokeAgentScope(string clientAppId, string tenantId, string? clientDisplayName, string? correlationId, string? promptContent = null);
    IDisposable? StartInferenceScope(LogIngestRequest request, string? clientDisplayName);
}

/// <summary>
/// Concrete implementation of A365 observability service.
/// Currently a no-op stub pending SDK API stabilization.
/// </summary>
public sealed class Agent365ObservabilityService : IAgent365ObservabilityService
{
    private readonly TokenCredential _credential;
    private readonly ILogger<Agent365ObservabilityService> _logger;

    public Agent365ObservabilityService(
        TokenCredential credential,
        ILogger<Agent365ObservabilityService> logger)
    {
        _credential = credential;
        _logger = logger;
    }

    public IDisposable? StartInvokeAgentScope(
        string clientAppId,
        string tenantId,
        string? clientDisplayName,
        string? correlationId,
        string? promptContent = null)
    {
        // TODO: Once SDK exposes InvokeAgentScope.Start, implement here
        _logger.LogTrace("A365 InvokeAgentScope stub: {ClientAppId}/{TenantId}", clientAppId, tenantId);
        return null;
    }

    public IDisposable? StartInferenceScope(LogIngestRequest request, string? clientDisplayName)
    {
        // TODO: Once SDK exposes InferenceScope.Start, implement here
        _logger.LogTrace("A365 InferenceScope stub: {ClientAppId}/{TenantId}", request.ClientAppId, request.TenantId);
        return null;
    }
}

/// <summary>
/// No-op implementation when A365 observability is disabled.
/// </summary>
public sealed class NoOpAgent365ObservabilityService : IAgent365ObservabilityService
{
    public IDisposable? StartInvokeAgentScope(string clientAppId, string tenantId, string? clientDisplayName, string? correlationId, string? promptContent = null) => null;
    public IDisposable? StartInferenceScope(LogIngestRequest request, string? clientDisplayName) => null;
}
