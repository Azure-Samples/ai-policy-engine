namespace AIPolicyEngine.Api.Models;

public sealed class TraceRecord
{
    public DateTime Timestamp { get; set; }
    public string DeploymentId { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public string CostToUs { get; set; } = "0.00";
    public string CostToCustomer { get; set; } = "0.00";
    public bool IsOverbilled { get; set; }
    public int StatusCode { get; set; } = 200;
}
