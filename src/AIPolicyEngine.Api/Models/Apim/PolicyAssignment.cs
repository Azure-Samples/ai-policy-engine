using System.Text.Json;

namespace AIPolicyEngine.Api.Models.Apim;

public sealed class PolicyAssignment
{
    public string Id { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = "policy-assignment";
    public string ApiId { get; set; } = string.Empty;
    public string? OperationId { get; set; }
    public string ApiDisplayName { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string TemplateVersion { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Parameters { get; set; } = [];
    public string? GeneratedXmlHash { get; set; }
    public DateTime? LastAppliedAt { get; set; }
    public string AppliedBy { get; set; } = string.Empty;
    public string Status { get; set; } = PolicyAssignmentStatuses.Pending;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string BuildId(string apiId, string? operationId)
        => $"pa:{apiId}:{operationId ?? "_all"}";
}
