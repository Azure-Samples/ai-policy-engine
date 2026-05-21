using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AIPolicyEngine.Api.Models.Apim;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Api.Services.ApimManagement;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AIPolicyEngine.Tests.ApimManagement;

public sealed class ApplyOrchestratorTests
{
    [Fact]
    public async Task QueueAndProcessApiPolicyApply_HappyPath_TransitionsPendingApplyingSynced()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary(xml: "<policies><inbound><base /></inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>");
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        catalog.GetApiAsync("api-1", Arg.Any<CancellationToken>())
            .Returns(new ApimApiSummary { Id = "api-1", DisplayName = "API One" });

        var request = new ApplyPolicyRequest
        {
            TemplateId = "entra-jwt-ai",
            Parameters = new Dictionary<string, JsonElement>
            {
                ["ExpectedAudience"] = ToJson("api://engine")
            }
        };

        var response = await sut.QueueApiPolicyApplyAsync("api-1", request, "tester@contoso.com");
        await sut.ProcessAssignmentAsync("api-1", null);

        Assert.Equal("pa:api-1:_all", response.AssignmentId);
        Assert.Equal(PolicyAssignmentStatuses.Applying, response.Status);
        Assert.Equal([PolicyAssignmentStatuses.Pending, PolicyAssignmentStatuses.Applying, PolicyAssignmentStatuses.Synced], repo.UpsertHistory.Select(x => x.Status).ToArray());

        var stored = await repo.GetAsync("api-1", null);
        Assert.NotNull(stored);
        Assert.Equal(PolicyAssignmentStatuses.Synced, stored!.Status);
        Assert.Equal("1.0", stored.TemplateVersion);
        Assert.Equal("tester@contoso.com", stored.AppliedBy);
        Assert.Equal(ComputeSha256(templateLibrary.RenderedXml!), stored.GeneratedXmlHash);
        Assert.NotNull(stored.LastAppliedAt);
        await catalog.Received(1).ApplyApiPolicyAsync("api-1", templateLibrary.RenderedXml!, Arg.Any<CancellationToken>());

        Assert.True(channel.Reader.TryRead(out var workItem));
        Assert.Equal("api-1", workItem.ApiId);
        Assert.Null(workItem.OperationId);
    }

    [Fact]
    public async Task QueueOperationPolicyApply_HappyPath_CreatesScopedAssignment()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary();
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        catalog.GetApiAsync("api-1", Arg.Any<CancellationToken>())
            .Returns(new ApimApiSummary { Id = "api-1", DisplayName = "API One" });
        catalog.GetOperationAsync("api-1", "chat", Arg.Any<CancellationToken>())
            .Returns(new ApimOperationSummary { Id = "chat", DisplayName = "Chat", Method = "POST", UrlTemplate = "/chat" });

        var response = await sut.QueueOperationPolicyApplyAsync(
            "api-1",
            "chat",
            new ApplyPolicyRequest { TemplateId = "entra-jwt-ai", Parameters = new Dictionary<string, JsonElement>() },
            "tester@contoso.com");

        Assert.Equal("pa:api-1:chat", response.AssignmentId);
        var stored = await repo.GetAsync("api-1", "chat");
        Assert.NotNull(stored);
        Assert.Equal("chat", stored!.OperationId);
        Assert.True(channel.Reader.TryRead(out var workItem));
        Assert.Equal("chat", workItem.OperationId);
    }

    [Fact]
    public async Task ProcessAssignmentAsync_WhenOperationScoped_UsesOperationApplyCall()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary();
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        await repo.UpsertAsync(CreateAssignment(apiId: "api-1", operationId: "chat", status: PolicyAssignmentStatuses.Pending));

        await sut.ProcessAssignmentAsync("api-1", "chat");

        await catalog.Received(1).ApplyOperationPolicyAsync("api-1", "chat", templateLibrary.RenderedXml!, Arg.Any<CancellationToken>());
        await catalog.DidNotReceive().ApplyApiPolicyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAssignmentAsync_WhenCatalogApplyFails_MarksAssignmentFailedAndKeepsDocument()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary();
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        await repo.UpsertAsync(CreateAssignment(apiId: "api-fail", status: PolicyAssignmentStatuses.Pending));
        catalog.ApplyApiPolicyAsync("api-fail", templateLibrary.RenderedXml!, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("APIM write failed")));

        await sut.ProcessAssignmentAsync("api-fail", null);

        var stored = await repo.GetAsync("api-fail", null);
        Assert.NotNull(stored);
        Assert.Equal(PolicyAssignmentStatuses.Failed, stored!.Status);
        Assert.Equal("APIM write failed", stored.ErrorMessage);
        Assert.Null(stored.GeneratedXmlHash);
        Assert.Contains(repo.UpsertHistory, assignment => assignment.Status == PolicyAssignmentStatuses.Applying);
        Assert.Contains(repo.UpsertHistory, assignment => assignment.Status == PolicyAssignmentStatuses.Failed);
    }

    [Fact]
    public async Task ProcessAssignmentAsync_WhenAssignmentMissing_DoesNothing()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary();
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        await sut.ProcessAssignmentAsync("missing", null);

        Assert.Empty(repo.UpsertHistory);
        await catalog.DidNotReceive().ApplyApiPolicyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearApiPolicyAsync_DeletesAssignmentAndAppliesPassthrough()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary();
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        await repo.UpsertAsync(CreateAssignment(apiId: "api-clear"));
        catalog.GetApiAsync("api-clear", Arg.Any<CancellationToken>())
            .Returns(new ApimApiSummary { Id = "api-clear", DisplayName = "API Clear" });

        await sut.ClearApiPolicyAsync("api-clear");

        await catalog.Received(1).SetApiPassthroughPolicyAsync("api-clear", Arg.Any<CancellationToken>());
        Assert.Contains(repo.DeleteHistory, x => x == ("api-clear", null));
        Assert.Null(await repo.GetAsync("api-clear", null));
    }

    [Fact]
    public async Task ClearOperationPolicyAsync_DeletesAssignmentAndAppliesPassthrough()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary();
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        await repo.UpsertAsync(CreateAssignment(apiId: "api-clear", operationId: "chat"));
        catalog.GetOperationAsync("api-clear", "chat", Arg.Any<CancellationToken>())
            .Returns(new ApimOperationSummary { Id = "chat", DisplayName = "Chat", Method = "POST", UrlTemplate = "/chat" });

        await sut.ClearOperationPolicyAsync("api-clear", "chat");

        await catalog.Received(1).SetOperationPassthroughPolicyAsync("api-clear", "chat", Arg.Any<CancellationToken>());
        Assert.Contains(repo.DeleteHistory, x => x == ("api-clear", "chat"));
        Assert.Null(await repo.GetAsync("api-clear", "chat"));
    }

    [Fact]
    public async Task QueueApiPolicyApplyAsync_WhenApiMissing_ThrowsKeyNotFoundException()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary();
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.QueueApiPolicyApplyAsync(
                "missing-api",
                new ApplyPolicyRequest { TemplateId = "entra-jwt-ai", Parameters = new Dictionary<string, JsonElement>() },
                "tester@contoso.com"));

        Assert.Equal("APIM API 'missing-api' was not found.", ex.Message);
    }

    [Fact]
    public async Task QueueApiPolicyApplyAsync_WhenTemplateIdMissing_ThrowsTemplateValidationException()
    {
        var sut = CreateSut(Substitute.For<IApimCatalogService>(), CreateTemplateLibrary(), new InMemoryPolicyAssignmentRepository(), Channel.CreateUnbounded<ApimPolicyApplyWorkItem>());

        var ex = await Assert.ThrowsAsync<TemplateValidationException>(() =>
            sut.QueueApiPolicyApplyAsync("api-1", new ApplyPolicyRequest(), "tester@contoso.com"));

        Assert.Equal("templateId is required.", ex.Message);
    }

    [Fact]
    public async Task QueueApiPolicyApplyAsync_ConcurrentWritesToSameApi_LastWriterWins()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary();
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        catalog.GetApiAsync("api-race", Arg.Any<CancellationToken>())
            .Returns(new ApimApiSummary { Id = "api-race", DisplayName = "API Race" });

        await sut.QueueApiPolicyApplyAsync("api-race", new ApplyPolicyRequest
        {
            TemplateId = "template-one",
            Parameters = new Dictionary<string, JsonElement> { ["ExpectedAudience"] = ToJson("api://one") }
        }, "tester@contoso.com");

        await sut.QueueApiPolicyApplyAsync("api-race", new ApplyPolicyRequest
        {
            TemplateId = "template-two",
            Parameters = new Dictionary<string, JsonElement> { ["ExpectedAudience"] = ToJson("api://two") }
        }, "tester@contoso.com");

        var stored = await repo.GetAsync("api-race", null);
        Assert.NotNull(stored);
        Assert.Equal("template-two", stored!.TemplateId);
        Assert.Equal("api://two", stored.Parameters["ExpectedAudience"].GetString());
        Assert.Equal(2, repo.UpsertHistory.Count(assignment => assignment.Status == PolicyAssignmentStatuses.Pending));
    }

    [Fact]
    public async Task ListRecoverableWorkItemsAsync_ReturnsPendingAndApplyingOnly()
    {
        var catalog = Substitute.For<IApimCatalogService>();
        var repo = new InMemoryPolicyAssignmentRepository();
        var channel = Channel.CreateUnbounded<ApimPolicyApplyWorkItem>();
        var templateLibrary = CreateTemplateLibrary();
        var sut = CreateSut(catalog, templateLibrary, repo, channel);

        await repo.UpsertAsync(CreateAssignment(apiId: "api-pending", status: PolicyAssignmentStatuses.Pending));
        await repo.UpsertAsync(CreateAssignment(apiId: "api-applying", operationId: "chat", status: PolicyAssignmentStatuses.Applying));
        await repo.UpsertAsync(CreateAssignment(apiId: "api-synced", status: PolicyAssignmentStatuses.Synced));

        var workItems = await sut.ListRecoverableWorkItemsAsync();

        Assert.Equal(2, workItems.Count);
        Assert.Contains(workItems, item => item.ApiId == "api-pending" && item.OperationId is null);
        Assert.Contains(workItems, item => item.ApiId == "api-applying" && item.OperationId == "chat");
    }

    private static ApimPolicyApplyService CreateSut(
        IApimCatalogService catalog,
        StubTemplateLibrary templateLibrary,
        InMemoryPolicyAssignmentRepository repo,
        Channel<ApimPolicyApplyWorkItem> channel)
        => new(catalog, templateLibrary, repo, channel, Substitute.For<ILogger<ApimPolicyApplyService>>());

    private static StubTemplateLibrary CreateTemplateLibrary(string? xml = null)
        => new(xml ?? "<policies><inbound><base /></inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>");

    private static PolicyAssignment CreateAssignment(string apiId, string? operationId = null, string status = PolicyAssignmentStatuses.Pending) => new()
    {
        Id = PolicyAssignment.BuildId(apiId, operationId),
        PartitionKey = "policy-assignment",
        ApiId = apiId,
        OperationId = operationId,
        ApiDisplayName = $"API {apiId}",
        TemplateId = "entra-jwt-ai",
        TemplateVersion = "1.0",
        Parameters = new Dictionary<string, JsonElement>
        {
            ["ExpectedAudience"] = ToJson("api://engine")
        },
        AppliedBy = "tester@contoso.com",
        Status = status,
        CreatedAt = new DateTime(2026, 05, 21, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 05, 21, 12, 0, 0, DateTimeKind.Utc)
    };

    private static string ComputeSha256(string xml)
        => $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant()}";

    private static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, JsonConfig.Default);

    private sealed class StubTemplateLibrary(string xml) : ITemplateLibraryService
    {
        public string? RenderedXml { get; } = xml;

        public Task<IReadOnlyList<TemplateManifest>> ListTemplatesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TemplateManifest>>([]);

        public Task<RenderedTemplate> RenderAsync(string templateId, IReadOnlyDictionary<string, JsonElement> parameters, CancellationToken ct = default)
            => Task.FromResult(new RenderedTemplate
            {
                Manifest = new TemplateManifest
                {
                    Id = templateId,
                    DisplayName = templateId,
                    Version = "1.0",
                    Scope = "api"
                },
                Parameters = parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                Xml = RenderedXml!
            });
    }

    private sealed class InMemoryPolicyAssignmentRepository : IPolicyAssignmentRepository
    {
        private readonly Dictionary<string, PolicyAssignment> _store = new(StringComparer.Ordinal);

        public List<PolicyAssignment> UpsertHistory { get; } = [];
        public List<(string ApiId, string? OperationId)> DeleteHistory { get; } = [];

        public Task<PolicyAssignment?> GetAsync(string apiId, string? operationId, CancellationToken ct = default)
        {
            var id = PolicyAssignment.BuildId(apiId, operationId);
            return Task.FromResult(_store.TryGetValue(id, out var assignment) ? Clone(assignment) : null);
        }

        public Task<List<PolicyAssignment>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(_store.Values.Select(Clone).ToList());

        public Task<PolicyAssignment> UpsertAsync(PolicyAssignment assignment, CancellationToken ct = default)
        {
            assignment.Id = PolicyAssignment.BuildId(assignment.ApiId, assignment.OperationId);
            assignment.PartitionKey = "policy-assignment";
            var clone = Clone(assignment);
            _store[clone.Id] = clone;
            UpsertHistory.Add(Clone(clone));
            return Task.FromResult(Clone(clone));
        }

        public Task<bool> DeleteAsync(string apiId, string? operationId, CancellationToken ct = default)
        {
            DeleteHistory.Add((apiId, operationId));
            return Task.FromResult(_store.Remove(PolicyAssignment.BuildId(apiId, operationId)));
        }

        private static PolicyAssignment Clone(PolicyAssignment assignment)
            => JsonSerializer.Deserialize<PolicyAssignment>(JsonSerializer.Serialize(assignment, JsonConfig.Default), JsonConfig.Default)!;
    }
}
