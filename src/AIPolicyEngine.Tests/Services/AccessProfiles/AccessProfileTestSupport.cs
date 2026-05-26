using System.Reflection;
using System.Text.Json;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using Xunit.Sdk;

namespace AIPolicyEngine.Tests.Services.AccessProfiles;

internal static class AccessProfileTestSupport
{
    private static readonly Assembly ApiAssembly = typeof(PlanData).Assembly;

    public static Type RequireType(string typeName)
        => ApiAssembly.GetTypes().FirstOrDefault(type => string.Equals(type.Name, typeName, StringComparison.Ordinal))
           ?? throw new XunitException($"Missing contract type '{typeName}'.");

    public static object CreateAccessProfile(
        string clientAppId,
        string tenantId,
        string apiId,
        string? operationId,
        string planId,
        string? routingPolicyId = null,
        IEnumerable<string>? allowedDeployments = null,
        bool enabled = true)
    {
        var accessProfileType = RequireType("AccessProfile");
        var accessProfile = Activator.CreateInstance(accessProfileType)
            ?? throw new XunitException("Failed to construct AccessProfile model.");

        SetProperty(accessProfile, "Id", $"ap:{clientAppId}:{tenantId}:{apiId}:{operationId ?? "_all"}");
        SetProperty(accessProfile, "PartitionKey", "access-profile");
        SetProperty(accessProfile, "ClientAppId", clientAppId);
        SetProperty(accessProfile, "TenantId", tenantId);
        SetProperty(accessProfile, "ApiId", apiId);
        SetProperty(accessProfile, "OperationId", operationId);
        SetProperty(accessProfile, "PlanId", planId);
        SetProperty(accessProfile, "RoutingPolicyId", routingPolicyId);
        SetProperty(accessProfile, "AllowedDeployments", (allowedDeployments ?? []).ToList());
        SetProperty(accessProfile, "Enabled", enabled);
        SetProperty(accessProfile, "CreatedAt", new DateTime(2026, 05, 21, 12, 0, 0, DateTimeKind.Utc));
        SetProperty(accessProfile, "UpdatedAt", new DateTime(2026, 05, 21, 12, 0, 0, DateTimeKind.Utc));
        SetProperty(accessProfile, "CreatedBy", "tester@contoso.com");
        return accessProfile;
    }

    public static AccessProfileResolverHarness CreateResolverHarness(
        IEnumerable<object> accessProfiles,
        ClientPlanAssignment? legacyAssignment)
        => CreateResolverHarness(
            accessProfiles,
            legacyAssignment is null ? [] : [legacyAssignment]);

    public static AccessProfileResolverHarness CreateResolverHarness(
        IEnumerable<object> accessProfiles,
        IEnumerable<ClientPlanAssignment> legacyAssignments)
    {
        var resolverInterface = RequireType("IAccessProfileResolver");
        var resolverImplementation = ApiAssembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && resolverInterface.IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new XunitException("Missing concrete IAccessProfileResolver implementation.");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        var fakeRedis = new FakeRedis();
        services.AddSingleton<IConnectionMultiplexer>(fakeRedis.Multiplexer);
        services.AddSingleton<IRepository<ClientPlanAssignment>>(new InMemoryClientAssignmentRepository(legacyAssignments));

        RegisterConstructorDependencies(services, resolverImplementation, accessProfiles.ToList());

        var provider = services.BuildServiceProvider();
        var resolver = ActivatorUtilities.CreateInstance(provider, resolverImplementation);
        return new AccessProfileResolverHarness(resolver, resolverInterface);
    }

    public static (object Proxy, ResolverInvocationTracker Tracker) CreateResolverProxy(Func<string, string, string, string?, ResolvedAccessSnapshot?> onResolve)
    {
        var resolverType = RequireType("IAccessProfileResolver");
        var tracker = new ResolverInvocationTracker();
        var proxy = ReflectionProxy.Create(resolverType, (method, args) =>
        {
            if (string.Equals(method.Name, "ResolveAsync", StringComparison.Ordinal))
            {
                var clientAppId = (string?)args?[0] ?? string.Empty;
                var tenantId = (string?)args?[1] ?? string.Empty;
                var apiId = (string?)args?[2] ?? string.Empty;
                var operationId = args?.Length > 3 ? args?[3] as string : null;
                tracker.Calls.Add((clientAppId, tenantId, apiId, operationId));
                var snapshot = onResolve(clientAppId, tenantId, apiId, operationId);
                return CreateTaskResult(method.ReturnType, snapshot is null ? null : CreateResolvedAccess(snapshot));
            }

            throw new NotSupportedException($"Resolver proxy does not support method '{method.Name}'.");
        });

        return (proxy, tracker);
    }

    public static object? CreateResolvedAccess(ResolvedAccessSnapshot snapshot)
    {
        var resolvedType = ApiAssembly.GetTypes().FirstOrDefault(type => string.Equals(type.Name, "ResolvedAccessProfile", StringComparison.Ordinal))
            ?? throw new XunitException("Missing contract type 'ResolvedAccessProfile'. Spec appendix still mentions 'ResolvedAccess'; align the contract.");

        var resolved = Activator.CreateInstance(resolvedType)
            ?? throw new XunitException("Failed to construct ResolvedAccessProfile.");
        SetProperty(resolved, "PlanId", snapshot.PlanId);
        SetProperty(resolved, "RoutingPolicyId", snapshot.RoutingPolicyId);
        SetProperty(resolved, "AllowedDeployments", snapshot.AllowedDeployments.ToList());
        SetProperty(resolved, "SourceProfileId", snapshot.SourceProfileId);
        return resolved;
    }

    public static string? GetString(object? instance, string propertyName)
        => instance is null ? null : GetProperty(instance, propertyName)?.ToString();

    public static IReadOnlyList<string> GetStrings(object instance, string propertyName)
    {
        var value = GetProperty(instance, propertyName);
        return value switch
        {
            null => [],
            IEnumerable<string> strings => strings.ToList(),
            _ => throw new XunitException($"Property '{propertyName}' is not a string list.")
        };
    }

    private static void RegisterConstructorDependencies(IServiceCollection services, Type implementationType, IReadOnlyList<object> accessProfiles)
    {
        foreach (var parameter in implementationType.GetConstructors()
                     .OrderByDescending(ctor => ctor.GetParameters().Length)
                     .First().GetParameters())
        {
            if (services.Any(descriptor => descriptor.ServiceType == parameter.ParameterType))
            {
                continue;
            }

            if (parameter.ParameterType == typeof(IConnectionMultiplexer) ||
                parameter.ParameterType == typeof(TimeProvider) ||
                parameter.ParameterType == typeof(ILoggerFactory))
            {
                continue;
            }

            if (parameter.ParameterType.IsGenericType &&
                parameter.ParameterType.GetGenericTypeDefinition() == typeof(ILogger<>))
            {
                continue;
            }

            if (parameter.ParameterType.IsGenericType &&
                parameter.ParameterType.GetGenericTypeDefinition() == typeof(IRepository<>) &&
                string.Equals(parameter.ParameterType.GenericTypeArguments[0].Name, nameof(ClientPlanAssignment), StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(parameter.ParameterType.Name, "IAccessProfileRepository", StringComparison.Ordinal))
            {
                services.AddSingleton(parameter.ParameterType, CreateAccessProfileRepositoryProxy(parameter.ParameterType, accessProfiles));
                continue;
            }

            if (parameter.ParameterType.IsGenericType &&
                parameter.ParameterType.GetGenericTypeDefinition() == typeof(IRepository<>) &&
                string.Equals(parameter.ParameterType.GenericTypeArguments[0].Name, "AccessProfile", StringComparison.Ordinal))
            {
                var repository = CreateGenericAccessProfileRepository(parameter.ParameterType.GenericTypeArguments[0], accessProfiles);
                services.AddSingleton(parameter.ParameterType, repository);
                continue;
            }

            if (parameter.ParameterType.IsInterface || parameter.ParameterType.IsAbstract)
            {
                services.AddSingleton(parameter.ParameterType, _ => Substitute.For([parameter.ParameterType], []));
                continue;
            }

            if (parameter.ParameterType.GetConstructor(Type.EmptyTypes) is not null)
            {
                services.AddSingleton(parameter.ParameterType, Activator.CreateInstance(parameter.ParameterType)!);
                continue;
            }

            throw new XunitException($"Unsupported constructor dependency '{parameter.ParameterType.FullName}' for resolver '{implementationType.Name}'.");
        }
    }

    private static object CreateGenericAccessProfileRepository(Type accessProfileType, IReadOnlyList<object> accessProfiles)
    {
        var repositoryType = typeof(ReflectionEntityRepository<>).MakeGenericType(accessProfileType);
        return Activator.CreateInstance(repositoryType, [accessProfiles])
            ?? throw new XunitException("Failed to construct generic access profile repository.");
    }

    private static object CreateAccessProfileRepositoryProxy(Type repositoryInterfaceType, IReadOnlyList<object> accessProfiles)
    {
        var store = accessProfiles.ToDictionary(profile => GetString(profile, "Id")!, CloneUntyped, StringComparer.Ordinal);
        return ReflectionProxy.Create(repositoryInterfaceType, (method, args) =>
        {
            if (string.Equals(method.Name, "GetAllAsync", StringComparison.Ordinal) ||
                string.Equals(method.Name, "ListAsync", StringComparison.Ordinal))
            {
                return CreateTaskResult(method.ReturnType, CreateTypedList(method.ReturnType, store.Values.ToList()));
            }

            if (string.Equals(method.Name, "GetAsync", StringComparison.Ordinal) ||
                string.Equals(method.Name, "FindAsync", StringComparison.Ordinal) ||
                string.Equals(method.Name, "ResolveAsync", StringComparison.Ordinal) ||
                string.Equals(method.Name, "GetForScopeAsync", StringComparison.Ordinal))
            {
                var match = FindProfile(store.Values, args);
                return CreateTaskResult(method.ReturnType, match is null ? null : CloneUntyped(match));
            }

            if (string.Equals(method.Name, "UpsertAsync", StringComparison.Ordinal))
            {
                var entity = CloneUntyped(args?[0] ?? throw new XunitException("UpsertAsync requires entity argument."));
                store[GetString(entity, "Id")!] = entity;
                return CreateTaskResult(method.ReturnType, CloneUntyped(entity));
            }

            if (string.Equals(method.Name, "DeleteAsync", StringComparison.Ordinal))
            {
                var id = args?.OfType<string>().FirstOrDefault();
                var deleted = id is not null && store.Remove(id);
                return CreateTaskResult(method.ReturnType, deleted);
            }

            throw new NotSupportedException($"Repository proxy does not support method '{method.Name}'.");
        });
    }

    private static object? FindProfile(IEnumerable<object> accessProfiles, object?[]? args)
    {
        var stringArgs = (args ?? []).OfType<string>().ToList();
        if (stringArgs.Count == 0)
        {
            return null;
        }

        if (stringArgs.Count == 1)
        {
            return accessProfiles.FirstOrDefault(profile =>
                string.Equals(GetString(profile, "Id"), stringArgs[0], StringComparison.Ordinal));
        }

        var clientAppId = stringArgs[0];
        var tenantId = stringArgs.Count > 1 ? stringArgs[1] : null;
        var apiId = stringArgs.Count > 2 ? stringArgs[2] : null;
        var operationId = stringArgs.Count > 3 ? stringArgs[3] : null;

        return accessProfiles.FirstOrDefault(profile =>
            string.Equals(GetString(profile, "ClientAppId"), clientAppId, StringComparison.Ordinal) &&
            string.Equals(GetString(profile, "TenantId"), tenantId, StringComparison.Ordinal) &&
            string.Equals(GetString(profile, "ApiId"), apiId, StringComparison.Ordinal) &&
            string.Equals(GetString(profile, "OperationId") ?? "_all", operationId ?? "_all", StringComparison.Ordinal));
    }

    private static object CreateTaskResult(Type returnType, object? result)
    {
        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>))
        {
            throw new XunitException($"Unsupported async return type '{returnType.FullName}'.");
        }

        var resultType = returnType.GenericTypeArguments[0];
        var taskFromResult = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Task.FromResult) && method.IsGenericMethod)
            .MakeGenericMethod(resultType);
        return taskFromResult.Invoke(null, [result])!;
    }

    private static object CreateTypedList(Type taskReturnType, IReadOnlyList<object> items)
    {
        var listType = taskReturnType.GenericTypeArguments[0];
        var elementType = listType.IsGenericType ? listType.GenericTypeArguments[0] : RequireType("AccessProfile");
        var typedList = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var item in items)
        {
            typedList.Add(CloneUntyped(item));
        }

        return typedList;
    }

    private static object? GetProperty(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(instance);

    private static void SetProperty(object instance, string propertyName, object? value)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? throw new XunitException($"Property '{propertyName}' was not found on type '{instance.GetType().Name}'.");
        property.SetValue(instance, value);
    }

    private static object CloneUntyped(object instance)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(instance, instance.GetType(), JsonConfig.Default), instance.GetType(), JsonConfig.Default)
           ?? throw new XunitException($"Failed to clone instance of '{instance.GetType().Name}'.");

    internal sealed record ResolvedAccessSnapshot(string PlanId, string? RoutingPolicyId, IReadOnlyList<string> AllowedDeployments, string? SourceProfileId);

    internal sealed class ResolverInvocationTracker
    {
        public List<(string ClientAppId, string TenantId, string ApiId, string? OperationId)> Calls { get; } = [];
    }

    internal sealed class AccessProfileResolverHarness(object resolver, Type interfaceType) : IDisposable
    {
        private readonly MethodInfo _resolveMethod = interfaceType.GetMethod("ResolveAsync")
            ?? throw new XunitException("IAccessProfileResolver.ResolveAsync was not found.");

        public object Instance => resolver;

        public async Task<ResolvedAccessSnapshot?> ResolveAsync(string clientAppId, string tenantId, string apiId, string? operationId)
        {
            var invocation = _resolveMethod.Invoke(resolver, [clientAppId, tenantId, apiId, operationId, CancellationToken.None])
                ?? throw new XunitException("ResolveAsync returned null task.");
            await (Task)invocation;
            var result = invocation.GetType().GetProperty("Result")?.GetValue(invocation);
            if (result is null)
            {
                return null;
            }

            return new ResolvedAccessSnapshot(
                GetString(result, "PlanId") ?? throw new XunitException("Resolved access is missing PlanId."),
                GetString(result, "RoutingPolicyId"),
                GetStrings(result, "AllowedDeployments"),
                GetString(result, "SourceProfileId"));
        }

        public void Dispose()
        {
            if (resolver is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private class ReflectionProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler(targetMethod ?? throw new XunitException("Missing target method."), args);

        public static object Create(Type interfaceType, Func<MethodInfo, object?[]?, object?> handler)
        {
            var createMethod = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method =>
                    string.Equals(method.Name, nameof(DispatchProxy.Create), StringComparison.Ordinal) &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 2 &&
                    method.GetParameters().Length == 0);
            var proxy = (ReflectionProxy)createMethod.MakeGenericMethod(interfaceType, typeof(ReflectionProxy)).Invoke(null, null)!;
            proxy.Handler = handler;
            return proxy;
        }
    }

    private sealed class InMemoryClientAssignmentRepository(IEnumerable<ClientPlanAssignment> assignments) : IRepository<ClientPlanAssignment>
    {
        private readonly Dictionary<string, ClientPlanAssignment> _store = assignments
            .ToDictionary(assignment => $"{assignment.ClientAppId}:{assignment.TenantId}", Clone, StringComparer.Ordinal);

        public Task<ClientPlanAssignment?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_store.TryGetValue(id, out var value) ? Clone(value) : null);

        public Task<List<ClientPlanAssignment>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(_store.Values.Select(Clone).ToList());

        public Task<ClientPlanAssignment> UpsertAsync(ClientPlanAssignment entity, CancellationToken ct = default)
        {
            _store[$"{entity.ClientAppId}:{entity.TenantId}"] = Clone(entity);
            return Task.FromResult(Clone(entity));
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_store.Remove(id));

        private static ClientPlanAssignment Clone(ClientPlanAssignment entity)
            => JsonSerializer.Deserialize<ClientPlanAssignment>(JsonSerializer.Serialize(entity, JsonConfig.Default), JsonConfig.Default)!;
    }

    private sealed class ReflectionEntityRepository<T> : IRepository<T> where T : class
    {
        private readonly Dictionary<string, T> _store;

        public ReflectionEntityRepository(IEnumerable<object> seed)
        {
            _store = seed.Cast<T>().ToDictionary(GetId, Clone, StringComparer.Ordinal);
        }

        public Task<T?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_store.TryGetValue(id, out var entity) ? Clone(entity) : null);

        public Task<List<T>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(_store.Values.Select(Clone).ToList());

        public Task<T> UpsertAsync(T entity, CancellationToken ct = default)
        {
            _store[GetId(entity)] = Clone(entity);
            return Task.FromResult(Clone(entity));
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_store.Remove(id));

        private static string GetId(T entity)
            => (string?)typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(entity)
               ?? throw new XunitException($"Type '{typeof(T).Name}' does not expose an Id property.");

        private static T Clone(T entity)
            => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(entity, JsonConfig.Default), JsonConfig.Default)!;
    }
}
