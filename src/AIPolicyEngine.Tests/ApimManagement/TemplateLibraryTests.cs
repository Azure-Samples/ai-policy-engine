using System.Text.Json;
using AIPolicyEngine.Api.Models.Apim;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Api.Services.ApimManagement;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AIPolicyEngine.Tests.ApimManagement;

public sealed class TemplateLibraryTests
{
    [Fact]
    public async Task ListTemplatesAsync_LoadsAllShippedTemplates()
    {
        using var root = TemplateRoot.ForRepositoryTemplates();

        var templates = await root.Service.ListTemplatesAsync();

        Assert.Equal(8, templates.Count);
        Assert.Equal(
            ["entra-jwt-ai", "entra-jwt-ai-dlp", "entra-jwt-rest", "keycloak-jwt-ai", "keycloak-jwt-ai-dlp", "keycloak-jwt-rest", "subscription-key-ai", "subscription-key-ai-dlp"],
            templates.Select(template => template.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ListTemplatesAsync_ReturnsContractShape()
    {
        using var root = TemplateRoot.ForRepositoryTemplates();

        var response = new TemplateListResponse
        {
            Templates = (await root.Service.ListTemplatesAsync()).ToList()
        };

        Assert.All(response.Templates, template =>
        {
            Assert.False(string.IsNullOrWhiteSpace(template.Id));
            Assert.False(string.IsNullOrWhiteSpace(template.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(template.Version));
            Assert.Equal("api", template.Scope);
            Assert.NotNull(template.Parameters);
            Assert.All(template.Parameters, parameter =>
            {
                Assert.False(string.IsNullOrWhiteSpace(parameter.Name));
                Assert.False(string.IsNullOrWhiteSpace(parameter.Type));
                Assert.NotNull(parameter.Description);
            });
        });
    }

    [Fact]
    public async Task ListTemplatesAsync_UsesRepoRootFallbackWhenContentRootPointsAtApiProject()
    {
        using var root = TemplateRoot.ForApiProjectContentRoot();

        var templates = await root.Service.ListTemplatesAsync();

        Assert.Equal(8, templates.Count);
    }

    [Fact]
    public async Task ListTemplatesAsync_WhenManifestFileMissing_ThrowsInvalidOperationException()
    {
        using var root = TemplateRootBuilder.Create(templateId: "missing-manifest")
            .WithPolicy(EmptyPoliciesXml)
            .Build(writeManifest: false, writePolicy: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => root.Service.ListTemplatesAsync());

        Assert.Contains("must contain both template.json and policy.xml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListTemplatesAsync_WhenPolicyFileMissing_ThrowsInvalidOperationException()
    {
        using var root = TemplateRootBuilder.Create(templateId: "missing-policy")
            .WithManifest("missing-policy", "Missing Policy")
            .Build(writeManifest: true, writePolicy: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => root.Service.ListTemplatesAsync());

        Assert.Contains("must contain both template.json and policy.xml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListTemplatesAsync_WhenManifestOmitsPolicyPlaceholder_ThrowsInvalidOperationException()
    {
        using var root = TemplateRootBuilder.Create(templateId: "manifest-mismatch")
            .WithManifest("manifest-mismatch", "Manifest Mismatch")
            .WithPolicy(PolicyWithPlaceholder("Missing"))
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => root.Service.ListTemplatesAsync());

        Assert.Contains("missing parameter(s) referenced by policy.xml: Missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListTemplatesAsync_WhenManifestDeclaresExtraParameter_ThrowsInvalidOperationException()
    {
        using var root = TemplateRootBuilder.Create(templateId: "extra-param")
            .WithManifest(
                "extra-param",
                "Extra Param",
                new TemplateParameterDefinition { Name = "Unused", Type = "string", Required = true })
            .WithPolicy(EmptyPoliciesXml)
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => root.Service.ListTemplatesAsync());

        Assert.Contains("declares parameter(s) not present in policy.xml: Unused", ex.Message, StringComparison.Ordinal);
    }

    private static string PolicyWithPlaceholder(string name)
        => $"<policies>\n  <inbound>\n    <set-header name=\"x-test\" exists-action=\"override\"><value>{{{{{name}}}}}</value></set-header>\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>";

    private const string EmptyPoliciesXml = "<policies><inbound><base /></inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>";

    private sealed class TemplateRoot : IDisposable
    {
        public TemplateRoot(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            Service = new TemplateLibraryService(new TestHostEnvironment(contentRootPath), Substitute.For<ILogger<TemplateLibraryService>>());
        }

        public string ContentRootPath { get; }
        public TemplateLibraryService Service { get; }

        public static TemplateRoot ForRepositoryTemplates() => new(FindRepositoryRoot());

        public static TemplateRoot ForApiProjectContentRoot()
            => new(Path.Combine(FindRepositoryRoot(), "src", "AIPolicyEngine.Api"));

        public void Dispose()
        {
            if (Directory.Exists(ContentRootPath) && ContentRootPath.Contains("ApimManagementTestData", StringComparison.Ordinal))
            {
                Directory.Delete(ContentRootPath, recursive: true);
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "policies", "templates")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Repository root with policies\\templates was not found.");
        }
    }

    private sealed class TemplateRootBuilder
    {
        private readonly string _contentRootPath;
        private string _policyXml = EmptyPoliciesXml;
        private TemplateManifest _manifest;

        private TemplateRootBuilder(string contentRootPath, string templateId)
        {
            _contentRootPath = contentRootPath;
            _manifest = new TemplateManifest
            {
                Id = templateId,
                DisplayName = templateId,
                Version = "1.0",
                Scope = "api"
            };
        }

        public static TemplateRootBuilder Create(string templateId)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ApimManagementTestData", Guid.NewGuid().ToString("N"));
            return new TemplateRootBuilder(path, templateId);
        }

        public TemplateRootBuilder WithManifest(string id, string displayName, params TemplateParameterDefinition[] parameters)
        {
            _manifest = new TemplateManifest
            {
                Id = id,
                DisplayName = displayName,
                Version = "1.0",
                Scope = "api",
                Parameters = parameters.ToList()
            };
            return this;
        }

        public TemplateRootBuilder WithPolicy(string policyXml)
        {
            _policyXml = policyXml;
            return this;
        }

        public TemplateRoot Build(bool writeManifest = true, bool writePolicy = true)
        {
            var templateDirectory = Path.Combine(_contentRootPath, "policies", "templates", _manifest.Id);
            Directory.CreateDirectory(templateDirectory);

            if (writeManifest)
            {
                File.WriteAllText(Path.Combine(templateDirectory, "template.json"), JsonSerializer.Serialize(_manifest, JsonConfig.Default));
            }

            if (writePolicy)
            {
                File.WriteAllText(Path.Combine(templateDirectory, "policy.xml"), _policyXml);
            }

            return new TemplateRoot(_contentRootPath);
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = nameof(TemplateLibraryTests);
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
