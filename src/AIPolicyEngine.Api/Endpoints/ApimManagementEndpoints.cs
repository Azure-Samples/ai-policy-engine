using System.Security.Claims;
using AIPolicyEngine.Api.Models.Apim;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Api.Services.ApimManagement;

namespace AIPolicyEngine.Api.Endpoints;

public static class ApimManagementEndpoints
{
    public static IEndpointRouteBuilder MapApimManagementEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/apim/apis", ListApis)
            .WithName("ListApimApis")
            .WithDescription("List APIs from the connected Azure API Management instance")
            .RequireAuthorization("AdminPolicy")
            .Produces<ApimApisResponse>();

        routes.MapGet("/api/apim/apis/{apiId}/operations", ListOperations)
            .WithName("ListApimApiOperations")
            .WithDescription("List operations for an Azure API Management API")
            .RequireAuthorization("AdminPolicy")
            .Produces<ApimOperationsResponse>()
            .Produces(StatusCodes.Status404NotFound);

        routes.MapGet("/api/apim/apis/{apiId}/policy", GetApiPolicy)
            .WithName("GetApimApiPolicy")
            .WithDescription("Get the current API-scoped APIM policy and engine assignment")
            .RequireAuthorization("AdminPolicy")
            .Produces<ApimPolicyResponse>()
            .Produces(StatusCodes.Status404NotFound);

        routes.MapGet("/api/apim/apis/{apiId}/operations/{operationId}/policy", GetOperationPolicy)
            .WithName("GetApimOperationPolicy")
            .WithDescription("Get the current operation-scoped APIM policy and engine assignment")
            .RequireAuthorization("AdminPolicy")
            .Produces<ApimPolicyResponse>()
            .Produces(StatusCodes.Status404NotFound);

        routes.MapGet("/api/apim/templates", ListTemplates)
            .WithName("ListApimTemplates")
            .WithDescription("List repo-shipped APIM policy templates")
            .RequireAuthorization("AdminPolicy")
            .Produces<TemplateListResponse>();

        routes.MapPost("/api/apim/apis/{apiId}/policy", ApplyApiPolicy)
            .WithName("ApplyApimApiPolicy")
            .WithDescription("Queue an API-scoped APIM policy assignment")
            .RequireAuthorization("AdminPolicy")
            .Produces<ApplyPolicyAcceptedResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapPost("/api/apim/apis/{apiId}/operations/{operationId}/policy", ApplyOperationPolicy)
            .WithName("ApplyApimOperationPolicy")
            .WithDescription("Queue an operation-scoped APIM policy assignment")
            .RequireAuthorization("AdminPolicy")
            .Produces<ApplyPolicyAcceptedResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapDelete("/api/apim/apis/{apiId}/policy", ClearApiPolicy)
            .WithName("ClearApimApiPolicy")
            .WithDescription("Clear the engine assignment and set the API policy to passthrough")
            .RequireAuthorization("AdminPolicy")
            .Produces<ClearPolicyResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapDelete("/api/apim/apis/{apiId}/operations/{operationId}/policy", ClearOperationPolicy)
            .WithName("ClearApimOperationPolicy")
            .WithDescription("Clear the engine assignment and set the operation policy to passthrough")
            .RequireAuthorization("AdminPolicy")
            .Produces<ClearPolicyResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListApis(
        IApimCatalogService catalogService,
        ILogger<ApimApisResponse> logger,
        CancellationToken ct)
    {
        try
        {
            var apis = await catalogService.ListApisAsync(ct);
            logger.LogInformation("Fetched {Count} APIM APIs", apis.Count);
            return Results.Json(new ApimApisResponse { Apis = apis.ToList() }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching APIM APIs");
            return Results.Json(new { error = "Failed to fetch APIM APIs" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ListOperations(
        string apiId,
        IApimCatalogService catalogService,
        ILogger<ApimOperationsResponse> logger,
        CancellationToken ct)
    {
        try
        {
            if (await catalogService.GetApiAsync(apiId, ct) is null)
            {
                return Results.NotFound(new { error = $"APIM API '{apiId}' not found" });
            }

            var operations = await catalogService.ListOperationsAsync(apiId, ct);
            logger.LogInformation("Fetched {Count} APIM operations for ApiId={ApiId}", operations.Count, apiId);
            return Results.Json(new ApimOperationsResponse { Operations = operations.ToList() }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching APIM operations for ApiId={ApiId}", apiId);
            return Results.Json(new { error = "Failed to fetch APIM operations" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetApiPolicy(
        string apiId,
        IApimCatalogService catalogService,
        IPolicyAssignmentRepository assignmentRepository,
        ILogger<ApimPolicyResponse> logger,
        CancellationToken ct)
    {
        try
        {
            if (await catalogService.GetApiAsync(apiId, ct) is null)
            {
                return Results.NotFound(new { error = $"APIM API '{apiId}' not found" });
            }

            var assignment = await assignmentRepository.GetAsync(apiId, null, ct);
            var currentXml = await catalogService.GetApiPolicyXmlAsync(apiId, ct) ?? string.Empty;

            return Results.Json(new ApimPolicyResponse
            {
                Assignment = assignment,
                CurrentXml = currentXml
            }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching APIM policy for ApiId={ApiId}", apiId);
            return Results.Json(new { error = "Failed to fetch APIM policy" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetOperationPolicy(
        string apiId,
        string operationId,
        IApimCatalogService catalogService,
        IPolicyAssignmentRepository assignmentRepository,
        ILogger<ApimPolicyResponse> logger,
        CancellationToken ct)
    {
        try
        {
            if (await catalogService.GetOperationAsync(apiId, operationId, ct) is null)
            {
                return Results.NotFound(new { error = $"APIM operation '{operationId}' not found on API '{apiId}'" });
            }

            var assignment = await assignmentRepository.GetAsync(apiId, operationId, ct);
            var currentXml = await catalogService.GetOperationPolicyXmlAsync(apiId, operationId, ct) ?? string.Empty;

            return Results.Json(new ApimPolicyResponse
            {
                Assignment = assignment,
                CurrentXml = currentXml
            }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching APIM operation policy for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
            return Results.Json(new { error = "Failed to fetch APIM operation policy" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ListTemplates(
        ITemplateLibraryService templateLibraryService,
        ILogger<TemplateListResponse> logger,
        CancellationToken ct)
    {
        try
        {
            var templates = await templateLibraryService.ListTemplatesAsync(ct);
            logger.LogInformation("Fetched {Count} APIM templates", templates.Count);
            return Results.Json(new TemplateListResponse { Templates = templates.ToList() }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching APIM templates");
            return Results.Json(new { error = "Failed to fetch APIM templates" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ApplyApiPolicy(
        string apiId,
        ApplyPolicyRequest body,
        ClaimsPrincipal user,
        IApimPolicyApplyService applyService,
        ILogger<ApplyPolicyAcceptedResponse> logger,
        CancellationToken ct)
    {
        try
        {
            var response = await applyService.QueueApiPolicyApplyAsync(apiId, body, GetAppliedBy(user), ct);
            logger.LogInformation("Queued APIM API policy apply for ApiId={ApiId}", apiId);
            return Results.Json(response, JsonConfig.Default, statusCode: StatusCodes.Status202Accepted);
        }
        catch (TemplateValidationException ex)
        {
            logger.LogWarning(ex, "Rejected APIM API policy apply for ApiId={ApiId}", apiId);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning(ex, "APIM API policy apply target not found for ApiId={ApiId}", apiId);
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error queueing APIM API policy apply for ApiId={ApiId}", apiId);
            return Results.Json(new { error = "Failed to queue APIM policy apply" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ApplyOperationPolicy(
        string apiId,
        string operationId,
        ApplyPolicyRequest body,
        ClaimsPrincipal user,
        IApimPolicyApplyService applyService,
        ILogger<ApplyPolicyAcceptedResponse> logger,
        CancellationToken ct)
    {
        try
        {
            var response = await applyService.QueueOperationPolicyApplyAsync(apiId, operationId, body, GetAppliedBy(user), ct);
            logger.LogInformation("Queued APIM operation policy apply for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
            return Results.Json(response, JsonConfig.Default, statusCode: StatusCodes.Status202Accepted);
        }
        catch (TemplateValidationException ex)
        {
            logger.LogWarning(ex, "Rejected APIM operation policy apply for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning(ex, "APIM operation policy apply target not found for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error queueing APIM operation policy apply for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
            return Results.Json(new { error = "Failed to queue APIM policy apply" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ClearApiPolicy(
        string apiId,
        IApimPolicyApplyService applyService,
        ILogger<ClearPolicyResponse> logger,
        CancellationToken ct)
    {
        try
        {
            await applyService.ClearApiPolicyAsync(apiId, ct);
            logger.LogInformation("Cleared APIM API policy for ApiId={ApiId}", apiId);
            return Results.Json(new ClearPolicyResponse(), JsonConfig.Default);
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning(ex, "APIM API clear target not found for ApiId={ApiId}", apiId);
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error clearing APIM API policy for ApiId={ApiId}", apiId);
            return Results.Json(new { error = "Failed to clear APIM policy" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ClearOperationPolicy(
        string apiId,
        string operationId,
        IApimPolicyApplyService applyService,
        ILogger<ClearPolicyResponse> logger,
        CancellationToken ct)
    {
        try
        {
            await applyService.ClearOperationPolicyAsync(apiId, operationId, ct);
            logger.LogInformation("Cleared APIM operation policy for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
            return Results.Json(new ClearPolicyResponse(), JsonConfig.Default);
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning(ex, "APIM operation clear target not found for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error clearing APIM operation policy for ApiId={ApiId} OperationId={OperationId}", apiId, operationId);
            return Results.Json(new { error = "Failed to clear APIM operation policy" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string GetAppliedBy(ClaimsPrincipal user)
        => user.FindFirst("preferred_username")?.Value
           ?? user.FindFirst(ClaimTypes.Upn)?.Value
           ?? user.FindFirst(ClaimTypes.Email)?.Value
           ?? user.Identity?.Name
           ?? "unknown";
}
