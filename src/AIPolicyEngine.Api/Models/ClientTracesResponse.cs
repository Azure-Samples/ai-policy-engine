namespace AIPolicyEngine.Api.Models;

public sealed class ClientTracesResponse
{
    public List<TraceRecord> Traces { get; set; } = [];
}
