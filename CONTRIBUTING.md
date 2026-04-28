# Contributing Guidelines

## Welcome Contributors!

Thank you for your interest in contributing to the Azure API Management AI Policy Engine Environment project.

## Code of Conduct

Please read and follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## How to Contribute

### Reporting Issues
- Use the GitHub issue tracker
- Provide detailed reproduction steps
- Include environment information
- Attach relevant logs or screenshots

### Suggesting Enhancements
- Open an issue with the "enhancement" label
- Provide clear use cases
- Include implementation suggestions
- Consider backward compatibility

### Contributing Code
1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests for new functionality
5. Update documentation
6. Submit a pull request

## Development Setup

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/) (for the React dashboard)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- Azure CLI
- Bicep CLI
- VS Code with C# Dev Kit extension (recommended)

### Local Development
```bash
# Clone your fork
git clone https://github.com/YOUR_USERNAME/ai-policy-engine.git
cd ai-policy-engine/dotnet

# Run the full stack with .NET Aspire
dotnet run --project AIPolicyEngine.AppHost
```

The Aspire dashboard opens automatically and orchestrates all services (API, Redis, etc.).

### Frontend Development (separate terminal)
```bash
cd src/aipolicyengine-ui
npm install
npm run dev
```

## Project Structure

```
demo/                  # Demo console app
src/
├── AIPolicyEngine.slnx              # Solution file
├── AIPolicyEngine.AppHost/          # .NET Aspire orchestrator
├── AIPolicyEngine.ServiceDefaults/  # OpenTelemetry + Azure Monitor
├── AIPolicyEngine.Api/              # Main API project
│   ├── Endpoints/               # Minimal API endpoints
│   ├── Models/                  # DTOs and data models
│   └── Services/                # Business logic
├── AIPolicyEngine.Tests/            # xUnit tests
├── AIPolicyEngine.Benchmarks/       # BenchmarkDotNet perf tests
├── AIPolicyEngine.LoadTest/         # NBomber load tests
├── AIPolicyEngine-ui/               # React/TypeScript dashboard
└── Dockerfile                   # Multi-stage Docker build
```

## Pull Request Process

### Before Submitting
- [ ] Tests pass (`dotnet test`)
- [ ] Documentation updated
- [ ] Code follows style guidelines
- [ ] Commit messages are clear

### PR Requirements
- Clear description of changes
- Link to related issues
- Screenshots for UI changes
- Breaking change notifications

## Testing

### Running Tests
```bash
cd src
dotnet test AIPolicyEngine.Tests
```

### Running Benchmarks
```bash
cd src
dotnet run -c Release --project AIPolicyEngine.Benchmarks -- --filter *
```

### Test Categories
- Unit tests for individual services and endpoints
- Integration tests for component interactions
- Load tests for scalability (NBomber)
- Benchmark tests for performance regression (BenchmarkDotNet)

## Code Quality Standards

### .NET Code Standards
- Use Minimal APIs with endpoint classes organized in `Endpoints/`
- Use `async`/`await` throughout — no blocking calls
- Use `System.Text.Json` for serialization (not Newtonsoft.Json)
- All API endpoints return JSON with camelCase property naming
- Use XML doc comments for public APIs

### Frontend Code Standards
- React + TypeScript + Tailwind CSS + shadcn/ui components
- Microsoft brand colors (`#0078D4` blue, no purple)
- All API calls use Entra Bearer token authentication

### Example Endpoint Pattern
```csharp
public static class PlansEndpoints
{
    public static void MapPlansEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/plans").RequireAuthorization();

        group.MapGet("/", async (ChargebackService service) =>
            Results.Ok(await service.GetPlansAsync()));

        group.MapPost("/", async (CreatePlanRequest request, ChargebackService service) =>
            Results.Created($"/api/plans/{plan.Id}", await service.CreatePlanAsync(request)));
    }
}
```

### Example Test Pattern
```csharp
public class ChargebackServiceTests
{
    [Fact]
    public async Task CalculateCost_WithValidUsage_ReturnsExpectedCost()
    {
        // Arrange
        var service = new ChargebackService(/* dependencies */);

        // Act
        var cost = await service.CalculateCostAsync(promptTokens: 100, completionTokens: 50);

        // Assert
        Assert.True(cost > 0);
    }
}
```

## Review Process

### Code Review
- All code changes require review
- Address reviewer feedback
- Maintain code quality standards
- Ensure security best practices

### Approval Process
- Maintainer approval required
- Automated tests must pass
- Documentation must be updated
- No merge conflicts

## Recognition

Contributors will be recognized in:
- CONTRIBUTORS.md file
- Release notes
- Project documentation
- Community highlights

Thank you for contributing!
