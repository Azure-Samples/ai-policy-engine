using System.Net;
using System.Reflection;
using System.Text.Json;
using AIPolicyEngine.Api.Models.Apim;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Api.Services.ApimManagement;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AIPolicyEngine.Tests.ApimManagement;

public sealed class PolicyAssignmentRepositoryTests
{
    [Fact]
    public void BuildId_FormatsIdsPerSpec()
    {
        Assert.Equal("pa:azure-openai:_all", PolicyAssignment.BuildId("azure-openai", null));
        Assert.Equal("pa:azure-openai:chat", PolicyAssignment.BuildId("azure-openai", "chat"));
    }

    [Fact]
    public async Task UpsertAsync_AssignsIdAndPartitionKey_AndSupportsRoundTripUpdate()
    {
        using var harness = new CosmosHarness();
        var repository = harness.CreateRepository();

        var assignment = CreateAssignment(apiId: "api-1");
        await repository.UpsertAsync(assignment);

        Assert.Equal("pa:api-1:_all", assignment.Id);
        Assert.Equal("policy-assignment", assignment.PartitionKey);

        var stored = await repository.GetAsync("api-1", null);
        Assert.NotNull(stored);
        Assert.Equal("template-a", stored!.TemplateId);

        stored.TemplateId = "template-b";
        stored.Parameters["ExpectedAudience"] = ToJson("api://updated");
        await repository.UpsertAsync(stored);

        var updated = await repository.GetAsync("api-1", null);
        Assert.NotNull(updated);
        Assert.Equal("template-b", updated!.TemplateId);
        Assert.Equal("api://updated", updated.Parameters["ExpectedAudience"].GetString());
    }

    [Fact]
    public async Task GetAsync_ApiScopeUsesAllSuffix()
    {
        using var harness = new CosmosHarness();
        var repository = harness.CreateRepository();
        await repository.UpsertAsync(CreateAssignment(apiId: "api-scope"));

        var result = await repository.GetAsync("api-scope", null);

        Assert.NotNull(result);
        await harness.Container.Received(1).ReadItemAsync<PolicyAssignment>(
            "pa:api-scope:_all",
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_OperationScopeUsesOperationIdSuffix()
    {
        using var harness = new CosmosHarness();
        var repository = harness.CreateRepository();
        await repository.UpsertAsync(CreateAssignment(apiId: "api-scope", operationId: "chat"));

        var result = await repository.GetAsync("api-scope", "chat");

        Assert.NotNull(result);
        await harness.Container.Received(1).ReadItemAsync<PolicyAssignment>(
            "pa:api-scope:chat",
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_ApiScopeUsesAllSuffix()
    {
        using var harness = new CosmosHarness();
        var repository = harness.CreateRepository();
        await repository.UpsertAsync(CreateAssignment(apiId: "api-delete"));

        var deleted = await repository.DeleteAsync("api-delete", null);

        Assert.True(deleted);
        await harness.Container.Received(1).DeleteItemAsync<PolicyAssignment>(
            "pa:api-delete:_all",
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_OperationScopeUsesOperationIdSuffix()
    {
        using var harness = new CosmosHarness();
        var repository = harness.CreateRepository();
        await repository.UpsertAsync(CreateAssignment(apiId: "api-delete", operationId: "chat"));

        var deleted = await repository.DeleteAsync("api-delete", "chat");

        Assert.True(deleted);
        await harness.Container.Received(1).DeleteItemAsync<PolicyAssignment>(
            "pa:api-delete:chat",
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_MissingAssignment_ReturnsNull()
    {
        using var harness = new CosmosHarness();
        var repository = harness.CreateRepository();

        var result = await repository.GetAsync("missing-api", null);

        Assert.Null(result);
    }

    private static PolicyAssignment CreateAssignment(string apiId, string? operationId = null) => new()
    {
        ApiId = apiId,
        OperationId = operationId,
        ApiDisplayName = $"Display {apiId}",
        TemplateId = "template-a",
        TemplateVersion = "1.0",
        Parameters = new Dictionary<string, JsonElement>
        {
            ["ExpectedAudience"] = ToJson("api://original")
        },
        AppliedBy = "tester@contoso.com",
        Status = PolicyAssignmentStatuses.Pending,
        CreatedAt = new DateTime(2026, 05, 21, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 05, 21, 12, 0, 0, DateTimeKind.Utc)
    };

    private static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, JsonConfig.Default);

    private sealed class CosmosHarness : IDisposable
    {
        private readonly Dictionary<string, PolicyAssignment> _store = new(StringComparer.Ordinal);
        private readonly CosmosClient _cosmosClient = Substitute.For<CosmosClient>();
        private readonly ConfigurationContainerProvider _provider;

        public CosmosHarness()
        {
            Container = Substitute.For<Container>();
            _cosmosClient.GetContainer("aipolicy", "configuration").Returns(Container);
            _provider = new ConfigurationContainerProvider(_cosmosClient, Substitute.For<ILogger<ConfigurationContainerProvider>>());
            typeof(ConfigurationContainerProvider)
                .GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_provider, true);

            Container.UpsertItemAsync(
                    Arg.Any<PolicyAssignment>(),
                    Arg.Any<PartitionKey>(),
                    Arg.Any<ItemRequestOptions?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var entity = Clone(callInfo.ArgAt<PolicyAssignment>(0));
                    _store[entity.Id] = Clone(entity);
                    return Task.FromResult(CreateItemResponse(entity));
                });

            Container.ReadItemAsync<PolicyAssignment>(
                    Arg.Any<string>(),
                    Arg.Any<PartitionKey>(),
                    Arg.Any<ItemRequestOptions?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var id = callInfo.ArgAt<string>(0);
                    if (!_store.TryGetValue(id, out var entity))
                    {
                        throw CreateNotFound();
                    }

                    return Task.FromResult(CreateItemResponse(Clone(entity)));
                });

            Container.DeleteItemAsync<PolicyAssignment>(
                    Arg.Any<string>(),
                    Arg.Any<PartitionKey>(),
                    Arg.Any<ItemRequestOptions?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var id = callInfo.ArgAt<string>(0);
                    if (!_store.Remove(id))
                    {
                        throw CreateNotFound();
                    }

                    return Task.FromResult(CreateItemResponse(new PolicyAssignment { Id = id }));
                });
        }

        public Container Container { get; }

        public CosmosPolicyAssignmentRepository CreateRepository()
            => new(_provider, Substitute.For<ILogger<CosmosPolicyAssignmentRepository>>());

        public void Dispose()
        {
            _store.Clear();
        }

        private static ItemResponse<PolicyAssignment> CreateItemResponse(PolicyAssignment assignment)
        {
            var response = Substitute.For<ItemResponse<PolicyAssignment>>();
            response.Resource.Returns(assignment);
            return response;
        }

        private static PolicyAssignment Clone(PolicyAssignment assignment)
            => JsonSerializer.Deserialize<PolicyAssignment>(JsonSerializer.Serialize(assignment, JsonConfig.Default), JsonConfig.Default)!;

        private static CosmosException CreateNotFound()
            => new("Not found", HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString("N"), 0);
    }
}
