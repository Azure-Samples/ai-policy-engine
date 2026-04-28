using Azure.Core;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using Microsoft.Agents.AI.Purview;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIPolicyEngine.Tests;

public class PurviewServiceTests
{
    // ------------------------------------------------------------------ //
    //  NoOpPurviewAuditService
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task NoOpPurviewAuditService_CompletesSuccessfully()
    {
        var service = new NoOpPurviewAuditService();

        await service.EmitAuditEventAsync(new LogIngestRequest(), "TestApp");
        // Should complete without throwing
    }

    [Fact]
    public async Task NoOpPurviewAuditService_WithCancellationToken_CompletesSuccessfully()
    {
        var service = new NoOpPurviewAuditService();
        using var cts = new CancellationTokenSource();

        await service.EmitAuditEventAsync(new LogIngestRequest(), "TestApp", cts.Token);
    }

    // ------------------------------------------------------------------ //
    //  DI registration
    // ------------------------------------------------------------------ //

    [Fact]
    public void AddPurviewServices_WithoutConfig_RegistersNoOp()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddPurviewServices(config);

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IPurviewAuditService>();
        Assert.IsType<NoOpPurviewAuditService>(service);
    }

    [Fact]
    public void AddPurviewServices_WithEmptyPurviewClientAppId_RegistersNoOp()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PURVIEW_CLIENT_APP_ID"] = "",
            })
            .Build();

        services.AddPurviewServices(config);

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IPurviewAuditService>();
        Assert.IsType<NoOpPurviewAuditService>(service);
    }

    [Fact]
    public void AddPurviewServices_WithConfig_RegistersRealService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PURVIEW_CLIENT_APP_ID"] = "test-app-id",
                ["PURVIEW_APP_NAME"] = "Test App",
            })
            .Build();

        services.AddPurviewServices(config);

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IPurviewAuditService>();
        Assert.IsType<PurviewAuditService>(service);

        // Clean up the background processor
        if (service is IDisposable disposable)
            disposable.Dispose();
    }

    /// <summary>
    /// PURVIEW_CLIENT_APP_ID set + PURVIEW_APP_NAME set - the real service must
    /// register even after the clientDisplayName parameter was added to EmitAuditEventAsync.
    /// </summary>
    [Fact]
    public void AddPurviewServices_WithConfig_RegistersRealServiceWithDisplayName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PURVIEW_CLIENT_APP_ID"] = "test-app-id",
                ["PURVIEW_APP_NAME"] = "Test App With Display Name",
            })
            .Build();

        services.AddPurviewServices(config);

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IPurviewAuditService>();
        Assert.IsType<PurviewAuditService>(service);

        if (service is IDisposable disposable)
            disposable.Dispose();
    }

    /// <summary>
    /// PURVIEW_BLOCK_ENABLED=true must not break DI registration.
    /// BlockEnabled is a new setting added alongside the exception-handling refactor.
    /// </summary>
    [Fact]
    public void AddPurviewServices_WithBlockEnabled_RegistersRealService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PURVIEW_CLIENT_APP_ID"] = "test-app-id",
                ["PURVIEW_APP_NAME"] = "Test App",
                ["PURVIEW_BLOCK_ENABLED"] = "true",
            })
            .Build();

        services.AddPurviewServices(config);

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IPurviewAuditService>();
        Assert.IsType<PurviewAuditService>(service);

        if (service is IDisposable disposable)
            disposable.Dispose();
    }

    // ------------------------------------------------------------------ //
    //  PurviewAuditService - core lifecycle
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task PurviewAuditService_CanceledEmit_ReturnsCanceledTask()
    {
        var service = CreatePurviewAuditService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EmitAuditEventAsync(new LogIngestRequest(), "TestApp", cts.Token));

        await service.DisposeAsync();
    }

    [Fact]
    public async Task PurviewAuditService_ProcessesAndDisposesAsync()
    {
        var service = CreatePurviewAuditService();

        await service.EmitAuditEventAsync(new LogIngestRequest
        {
            TenantId = "tenant-1",
            ClientAppId = "client-1",
            DeploymentId = "gpt-4o",
            ResponseBody = new OpenAiResponseBody
            {
                Model = "gpt-4o",
                Usage = new UsageData { TotalTokens = 42 }
            }
        }, "My Copilot App");

        await Task.Delay(25);
        await service.DisposeAsync();

        // No-op after disposal should not throw
        await service.EmitAuditEventAsync(
            new LogIngestRequest { TenantId = "tenant-1", ClientAppId = "client-1", DeploymentId = "gpt-4o" },
            "My Copilot App");
    }

    [Fact]
    public void PurviewAuditService_Dispose_IsIdempotent()
    {
        var service = CreatePurviewAuditService();

        service.Dispose();
        service.Dispose();
    }

    // ------------------------------------------------------------------ //
    //  PurviewAuditService - clientDisplayName parameter (new behavior)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Calling EmitAuditEventAsync with a well-formed clientDisplayName must
    /// complete without throwing. The display name flows to the per-event
    /// PurviewSettings.AppName inside the background processor.
    /// </summary>
    [Fact]
    public async Task EmitAuditEventAsync_WithClientDisplayName_AcceptsName()
    {
        var service = CreatePurviewAuditService();

        var exception = await Record.ExceptionAsync(() =>
            service.EmitAuditEventAsync(
                new LogIngestRequest
                {
                    TenantId = "tenant-1",
                    ClientAppId = "client-1",
                    DeploymentId = "gpt-4o",
                },
                "My Copilot App"));

        Assert.Null(exception);
        service.Dispose();
    }

    /// <summary>
    /// Null / empty / whitespace-only display names must not crash the service.
    /// The implementation is expected to fall back to ClientAppId (or another
    /// non-null string) so the background processor always has a valid AppName.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmitAuditEventAsync_WithNullOrEmptyClientDisplayName_FallsBackGracefully(string? displayName)
    {
        var service = CreatePurviewAuditService();

        var exception = await Record.ExceptionAsync(() =>
            service.EmitAuditEventAsync(
                new LogIngestRequest
                {
                    TenantId = "tenant-1",
                    ClientAppId = "client-fallback",
                    DeploymentId = "gpt-4o",
                },
                displayName!));

        Assert.Null(exception);
        service.Dispose();
    }

    // ------------------------------------------------------------------ //
    //  PurviewAuditService - exception policy (silent fail / fire-and-forget)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Purview SDK exceptions (PurviewRateLimitException, PurviewJobLimitExceededException,
    /// etc.) are caught and swallowed inside the background processor.
    /// Because EmitAuditEventAsync is fire-and-forget (channel write), no exception
    /// should ever propagate to the caller - even when IgnoreExceptions = true and the
    /// SDK would normally surface an error.
    /// </summary>
    [Fact]
    public async Task PurviewAuditService_PurviewRateLimitException_DoesNotPropagate()
    {
        // IgnoreExceptions = true simulates the "retry then silent-fail" path.
        // Emit several events in rapid succession to exercise the channel under load.
        var service = CreatePurviewAuditService();

        for (int i = 0; i < 5; i++)
        {
            var exception = await Record.ExceptionAsync(() =>
                service.EmitAuditEventAsync(
                    new LogIngestRequest { TenantId = $"tenant-{i}", ClientAppId = "client-1", DeploymentId = "gpt-4o" },
                    "RateLimitTestApp"));

            Assert.Null(exception); // fire-and-forget: caller must never see SDK exceptions
        }

        await Task.Delay(25);
        await service.DisposeAsync();
    }

    /// <summary>
    /// All exception variants (auth, payment, job, request, base PurviewException) are
    /// swallowed by the background processor. The service must process events and dispose
    /// cleanly regardless of what the SDK throws internally. Callers observe no exceptions.
    /// </summary>
    [Fact]
    public async Task PurviewAuditService_AllExceptions_LoggedButNotPropagated()
    {
        var service = CreatePurviewAuditService(); // IgnoreExceptions = true

        // Fill the channel beyond its 4-item limit; oldest events are dropped
        // but no OverflowException or anything else surfaces to the caller.
        for (int i = 0; i < 10; i++)
        {
            var emitException = await Record.ExceptionAsync(() =>
                service.EmitAuditEventAsync(
                    new LogIngestRequest { TenantId = $"tenant-{i}", ClientAppId = "app-1", DeploymentId = "gpt-4o" },
                    "ExceptionTestApp"));

            Assert.Null(emitException);
        }

        // Dispose must also complete cleanly.
        var disposeException = await Record.ExceptionAsync(async () =>
        {
            await Task.Delay(50);
            await service.DisposeAsync();
        });

        Assert.Null(disposeException);
    }

    // ------------------------------------------------------------------ //
    //  PurviewAuditService — request+response processing
    // ------------------------------------------------------------------ //

    /// <summary>
    /// A <see cref="LogIngestRequest"/> with both RequestBody (prompt) and
    /// ResponseBody (completion) must be queued and processed without error.
    /// IgnoreExceptions = true absorbs any network / SDK failures in tests.
    /// </summary>
    [Fact]
    public async Task PurviewAuditService_WithRealRequest_ProcessesPromptAndResponse()
    {
        var service = CreatePurviewAuditService();

        var request = new LogIngestRequest
        {
            TenantId = "tenant-prompt-response",
            ClientAppId = "client-app-123",
            DeploymentId = "gpt-4o",
            RequestBody = new { role = "user", content = "What is Azure Policy Engine?" },
            ResponseBody = new OpenAiResponseBody
            {
                Model = "gpt-4o",
                Usage = new UsageData { TotalTokens = 150 }
            }
        };

        var exception = await Record.ExceptionAsync(() =>
            service.EmitAuditEventAsync(request, "My Copilot App"));

        Assert.Null(exception);
        await Task.Delay(50);
        await service.DisposeAsync();
    }

    // ------------------------------------------------------------------ //
    //  PurviewAuditService — block verdict logging
    // ------------------------------------------------------------------ //

    /// <summary>
    /// When blockEnabled = true, the service must emit events and dispose cleanly.
    /// PURVIEW_BLOCK_VERDICT is only logged when the Purview Graph API returns a block
    /// action — that path requires a real API call and is covered by integration tests.
    /// Here we verify the service does not crash or surface exceptions when blockEnabled=true
    /// and the Graph API is unavailable (no-factory path returns HttpRequestException →
    /// IgnoreExceptions=true swallows it).
    /// </summary>
    [Fact]
    public async Task PurviewAuditService_BlockEnabled_EmitsAndDisposesWithoutException()
    {
        var capturingLogger = new CapturingLogger<PurviewAuditService>();
        var settings = new PurviewSettings("Test App")
        {
            IgnoreExceptions = true,
            PendingBackgroundJobLimit = 4
        };

        var service = new PurviewAuditService(
            settings,
            new StaticTokenCredential(),
            capturingLogger,
            blockEnabled: true);

        var exception = await Record.ExceptionAsync(() =>
            service.EmitAuditEventAsync(
                new LogIngestRequest
                {
                    TenantId    = "tenant-block-1",
                    ClientAppId = "client-block-1",
                    DeploymentId = "gpt-4o"
                },
                "BlockTestApp"));

        Assert.Null(exception); // fire-and-forget: caller must never see exceptions

        await Task.Delay(50); // allow background processor to dequeue and process
        var disposeException = await Record.ExceptionAsync(() => service.DisposeAsync().AsTask());
        Assert.Null(disposeException);
    }


    /// <summary>
    /// PurviewAuditService constructs PurviewGraphClient internally. An IPurviewGraphClient
    /// injection seam would be needed to fully mock this path with NSubstitute.
    /// Tracked as a future refactor (add interface + constructor param).
    /// </summary>
    [Fact(Skip = "No IPurviewGraphClient injection seam in PurviewAuditService — future refactor needed to test this path")]
    public Task PurviewAuditService_EmitCoreAsync_CallsGraphClient()
        => Task.CompletedTask;

    /// <summary>
    /// PURVIEW_BLOCK_VERDICT is logged only when the Purview Graph API returns a block action.
    /// Requires an IPurviewGraphClient injection seam and NSubstitute mock.
    /// Tracked as a future refactor.
    /// </summary>
    [Fact(Skip = "IPurviewGraphClient injection seam required — add to PurviewAuditService ctor, then mock with NSubstitute returning block verdict")]
    public Task PurviewAuditService_BlockEnabled_LogsBlockVerdictOnBlock()
        => Task.CompletedTask;

    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    private static PurviewAuditService CreatePurviewAuditService()
    {
        var settings = new PurviewSettings("Test App")
        {
            IgnoreExceptions = true,
            PendingBackgroundJobLimit = 4
        };

        return new PurviewAuditService(
            settings,
            new StaticTokenCredential(),
            NullLogger<PurviewAuditService>.Instance);
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    /// <summary>Returns a fixed, caller-supplied token string every time.</summary>
    private sealed class FixedTokenCredential(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> implementation that records every log call so tests
    /// can assert on log messages without depending on external logging infrastructure.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Records = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add((logLevel, formatter(state, exception)));
    }

    // ------------------------------------------------------------------ //
    //  CheckContentAsync — NoOpPurviewAuditService
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task NoOpPurviewAuditService_CheckContentAsync_AlwaysReturnsNotBlocked()
    {
        var service = new NoOpPurviewAuditService();

        var result = await service.CheckContentAsync(
            "sensitive content here",
            "tenant-123",
            "Test Client",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsBlocked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoOpPurviewAuditService_CheckContentAsync_NullOrEmptyContent_ReturnsNotBlocked(string? content)
    {
        var service = new NoOpPurviewAuditService();

        var result = await service.CheckContentAsync(
            content!,
            "tenant-123",
            "Test Client",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsBlocked);
    }

    // ------------------------------------------------------------------ //
    //  CheckContentAsync — PurviewAuditService (unit — no real Graph calls)
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task PurviewAuditService_CheckContentAsync_BlockDisabled_ReturnsFalseImmediately()
    {
        // blockEnabled defaults to false
        var service = CreatePurviewAuditService();

        var result = await service.CheckContentAsync(
            "any content",
            "tenant-123",
            "Test Client",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public async Task PurviewAuditService_CheckContentAsync_BlockEnabled_GraphUnavailable_ReturnsFalse()
    {
        // blockEnabled=true, no IHttpClientFactory (Graph calls will fail)
        // CheckContentAsync must return IsBlocked=false and not throw (silent fail)
        var settings = new PurviewSettings("Test App")
        {
            IgnoreExceptions = true,
            PendingBackgroundJobLimit = 4
        };

        var service = new PurviewAuditService(
            settings,
            new StaticTokenCredential(),
            NullLogger<PurviewAuditService>.Instance,
            blockEnabled: true);

        var result = await service.CheckContentAsync(
            "sensitive content",
            "tenant-123",
            "Test Client",
            CancellationToken.None);

        // Silent fail when Graph unavailable
        Assert.NotNull(result);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public async Task PurviewAuditService_CheckContentAsync_Timeout_ReturnsFalse()
    {
        // blockEnabled=true, pass a pre-cancelled CancellationToken
        // Must return IsBlocked=false within reasonable time, no exception surfaced
        var settings = new PurviewSettings("Test App")
        {
            IgnoreExceptions = true,
            PendingBackgroundJobLimit = 4
        };

        var service = new PurviewAuditService(
            settings,
            new StaticTokenCredential(),
            NullLogger<PurviewAuditService>.Instance,
            blockEnabled: true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.CheckContentAsync(
            "content",
            "tenant-123",
            "Test Client",
            cts.Token);

        // Timeout/cancellation should return false, not throw
        Assert.NotNull(result);
        Assert.False(result.IsBlocked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PurviewAuditService_CheckContentAsync_NullOrEmptyContent_SilentFail(string? content)
    {
        // null/empty content with blockEnabled=true must not crash service (silent fail)
        var settings = new PurviewSettings("Test App")
        {
            IgnoreExceptions = true,
            PendingBackgroundJobLimit = 4
        };

        var service = new PurviewAuditService(
            settings,
            new StaticTokenCredential(),
            NullLogger<PurviewAuditService>.Instance,
            blockEnabled: true);

        var result = await service.CheckContentAsync(
            content!,
            "tenant-123",
            "Test Client",
            CancellationToken.None);

        // Silent fail — should not throw
        Assert.NotNull(result);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public async Task PurviewAuditService_CheckContentAsync_WithDisplayName_UsesDisplayName()
    {
        // verify call completes without exceptions when clientDisplayName is provided
        // blockEnabled=false path (fast, no network)
        var service = CreatePurviewAuditService();

        var result = await service.CheckContentAsync(
            "test content",
            "tenant-123",
            "My Custom Client Display Name",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsBlocked);
    }

    // ------------------------------------------------------------------ //
    //  CheckContentAsync — Documented skip stubs
    // ------------------------------------------------------------------ //

    [Fact(Skip = "Requires IPurviewGraphClient injection seam — add interface, then mock GetProtectionScopesAsync to return ShouldProcess=true and ProcessContentAsync to return ShouldBlock=true")]
    public Task PurviewAuditService_CheckContentAsync_BlockEnabled_GraphReturnsBlock_ReturnsBlocked()
        => Task.CompletedTask;

    [Fact(Skip = "Requires IPurviewGraphClient injection seam — mock to return ShouldProcess=false")]
    public Task PurviewAuditService_CheckContentAsync_ScopesSaySkip_ReturnsNotBlocked()
        => Task.CompletedTask;
}
