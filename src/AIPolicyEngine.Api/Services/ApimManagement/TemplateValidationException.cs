namespace AIPolicyEngine.Api.Services.ApimManagement;

public sealed class TemplateValidationException : Exception
{
    public TemplateValidationException(string message) : base(message)
    {
    }
}
