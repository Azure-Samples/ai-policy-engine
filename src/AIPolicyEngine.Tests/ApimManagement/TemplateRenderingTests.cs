using System.Text.Json;
using AIPolicyEngine.Api.Models.Apim;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Api.Services.ApimManagement;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AIPolicyEngine.Tests.ApimManagement;

public sealed class TemplateRenderingTests
{
    private static readonly JsonSerializerOptions JsonOpts = JsonConfig.Default;

    [Fact]
    public async Task RenderAsync_SubstitutesProvidedParameters()
    {
        using var root = TemplateRootBuilder.Create(templateId: "sample-template")
            .WithManifest(
                "sample-template",
                "Sample",
                new TemplateParameterDefinition { Name = "Name", Type = "string", Required = true },
                new TemplateParameterDefinition { Name = "MaxCount", Type = "int", Required = true })
            .WithPolicy("""
<policies>
  <inbound>
    <set-header name="x-name" exists-action="override">
      <value>{{Name}}</value>
    </set-header>
    <set-header name="x-max" exists-action="override">
      <value>{{MaxCount}}</value>
    </set-header>
  </inbound>
  <backend><base /></backend>
  <outbound><base /></outbound>
  <on-error><base /></on-error>
</policies>
""")
            .Build();

        var rendered = await root.Service.RenderAsync("sample-template", new Dictionary<string, JsonElement>
        {
            ["Name"] = ToJson("Contoso"),
            ["MaxCount"] = ToJson(25)
        });

        Assert.Contains("<value>Contoso</value>", rendered.Xml, StringComparison.Ordinal);
        Assert.Contains("<value>25</value>", rendered.Xml, StringComparison.Ordinal);
        Assert.Equal("Contoso", rendered.Parameters["Name"].GetString());
        Assert.Equal(25, rendered.Parameters["MaxCount"].GetInt32());
    }

    [Fact]
    public async Task RenderAsync_WhenRequiredParameterMissing_ThrowsSpecificMessage()
    {
        using var root = TemplateRootBuilder.Create(templateId: "required-template")
            .WithManifest(
                "required-template",
                "Required",
                new TemplateParameterDefinition { Name = "Name", Type = "string", Required = true })
            .WithPolicy(PolicyWithPlaceholder("Name"))
            .Build();

        var ex = await Assert.ThrowsAsync<TemplateValidationException>(() =>
            root.Service.RenderAsync("required-template", new Dictionary<string, JsonElement>()));

        Assert.Equal("Template 'required-template' requires parameter 'Name'.", ex.Message);
    }

    [Fact]
    public async Task RenderAsync_WhenUnknownParameterProvided_ThrowsSpecificMessage()
    {
        using var root = TemplateRootBuilder.Create(templateId: "known-template")
            .WithManifest(
                "known-template",
                "Known",
                new TemplateParameterDefinition { Name = "Name", Type = "string", Required = true })
            .WithPolicy(PolicyWithPlaceholder("Name"))
            .Build();

        var ex = await Assert.ThrowsAsync<TemplateValidationException>(() =>
            root.Service.RenderAsync("known-template", new Dictionary<string, JsonElement>
            {
                ["Name"] = ToJson("Contoso"),
                ["Unexpected"] = ToJson("boom")
            }));

        Assert.Equal("Template 'known-template' does not define parameter(s): Unexpected.", ex.Message);
    }

    [Fact]
    public async Task RenderAsync_WhenIntParameterCannotParse_ThrowsValidationError()
    {
        using var root = TemplateRootBuilder.Create(templateId: "int-template")
            .WithManifest(
                "int-template",
                "Int",
                new TemplateParameterDefinition { Name = "Count", Type = "int", Required = true })
            .WithPolicy(PolicyWithPlaceholder("Count"))
            .Build();

        var ex = await Assert.ThrowsAsync<TemplateValidationException>(() =>
            root.Service.RenderAsync("int-template", new Dictionary<string, JsonElement>
            {
                ["Count"] = ToJson("not-an-int")
            }));

        Assert.Equal("Template 'int-template' parameter 'Count' must be of type 'int'.", ex.Message);
    }

    [Fact]
    public async Task RenderAsync_WhenIntParameterIsNumericString_NormalizesSuccessfully()
    {
        using var root = TemplateRootBuilder.Create(templateId: "numeric-string-template")
            .WithManifest(
                "numeric-string-template",
                "Numeric String",
                new TemplateParameterDefinition { Name = "Count", Type = "int", Required = true })
            .WithPolicy(PolicyWithPlaceholder("Count"))
            .Build();

        var rendered = await root.Service.RenderAsync("numeric-string-template", new Dictionary<string, JsonElement>
        {
            ["Count"] = ToJson("42")
        });

        Assert.Equal(42, rendered.Parameters["Count"].GetInt32());
        Assert.Contains("<value>42</value>", rendered.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_UsesDefaultValueWhenParameterOmitted()
    {
        using var root = TemplateRootBuilder.Create(templateId: "defaults-template")
            .WithManifest(
                "defaults-template",
                "Defaults",
                new TemplateParameterDefinition { Name = "Mode", Type = "string", Required = false, Default = ToJson("native") })
            .WithPolicy(PolicyWithPlaceholder("Mode"))
            .Build();

        var rendered = await root.Service.RenderAsync("defaults-template", new Dictionary<string, JsonElement>());

        Assert.Equal("native", rendered.Parameters["Mode"].GetString());
        Assert.Contains("<value>native</value>", rendered.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_ReplacesMultipleOccurrencesOfSamePlaceholder()
    {
        using var root = TemplateRootBuilder.Create(templateId: "repeat-template")
            .WithManifest(
                "repeat-template",
                "Repeat",
                new TemplateParameterDefinition { Name = "Audience", Type = "string", Required = true })
            .WithPolicy("""
<policies>
  <inbound>
    <set-header name="one" exists-action="override"><value>{{Audience}}</value></set-header>
    <set-header name="two" exists-action="override"><value>{{Audience}}</value></set-header>
  </inbound>
  <backend><base /></backend>
  <outbound><base /></outbound>
  <on-error><base /></on-error>
</policies>
""")
            .Build();

        var rendered = await root.Service.RenderAsync("repeat-template", new Dictionary<string, JsonElement>
        {
            ["Audience"] = ToJson("api://engine")
        });

        Assert.Equal(2, CountOccurrences(rendered.Xml, "api://engine"));
        Assert.DoesNotContain("{{Audience}}", rendered.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_WhitespacePlaceholderVariantRemainsLiteral()
    {
        using var root = TemplateRootBuilder.Create(templateId: "whitespace-template")
            .WithManifest(
                "whitespace-template",
                "Whitespace",
                new TemplateParameterDefinition { Name = "Name", Type = "string", Required = true })
            .WithPolicy("""
<policies>
  <inbound>
    <set-header name="tight" exists-action="override"><value>{{Name}}</value></set-header>
    <set-header name="spaced" exists-action="override"><value>{{ Name }}</value></set-header>
  </inbound>
  <backend><base /></backend>
  <outbound><base /></outbound>
  <on-error><base /></on-error>
</policies>
""")
            .Build();

        var rendered = await root.Service.RenderAsync("whitespace-template", new Dictionary<string, JsonElement>
        {
            ["Name"] = ToJson("Contoso")
        });

        Assert.Contains("<value>Contoso</value>", rendered.Xml, StringComparison.Ordinal);
        Assert.Contains("<value>{{ Name }}</value>", rendered.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_EmptyParameterDictionaryForTemplateWithoutRequiredParams_Succeeds()
    {
        using var root = TemplateRootBuilder.Create(templateId: "no-required-template")
            .WithManifest("no-required-template", "No Required")
            .WithPolicy(EmptyPoliciesXml)
            .Build();

        var rendered = await root.Service.RenderAsync("no-required-template", new Dictionary<string, JsonElement>());

        Assert.Equal(EmptyPoliciesXml, rendered.Xml);
        Assert.Empty(rendered.Parameters);
    }

    [Fact]
    public async Task RenderAsync_TemplateWithoutPlaceholders_PassesThroughLiteralXml()
    {
        using var root = TemplateRootBuilder.Create(templateId: "literal-template")
            .WithManifest("literal-template", "Literal")
            .WithPolicy("""
<policies>
  <inbound>
    <set-header name="x-mode" exists-action="override"><value>literal</value></set-header>
  </inbound>
  <backend><base /></backend>
  <outbound><base /></outbound>
  <on-error><base /></on-error>
</policies>
""")
            .Build();

        var rendered = await root.Service.RenderAsync("literal-template", new Dictionary<string, JsonElement>());

        Assert.Contains("<value>literal</value>", rendered.Xml, StringComparison.Ordinal);
        Assert.Equal(0, CountOccurrences(rendered.Xml, "{{"));
    }

    [Theory]
    [InlineData("entra-jwt-ai")]
    [InlineData("entra-jwt-ai-dlp")]
    [InlineData("subscription-key-ai")]
    [InlineData("subscription-key-ai-dlp")]
    [InlineData("entra-jwt-rest")]
    public async Task RenderAsync_ShippedTemplate_RendersSuccessfully(string templateId)
    {
        using var root = TemplateRoot.ForRepositoryTemplates();

        var templates = await root.Service.ListTemplatesAsync();
        var manifest = Assert.Single(templates, template => template.Id == templateId);

        var rendered = await root.Service.RenderAsync(templateId, BuildValidParameters(manifest));

        Assert.Equal(templateId, rendered.Manifest.Id);
        Assert.Contains("<policies>", rendered.Xml, StringComparison.Ordinal);
        Assert.Contains("</policies>", rendered.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", rendered.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("}}", rendered.Xml, StringComparison.Ordinal);
    }

    private static Dictionary<string, JsonElement> BuildValidParameters(TemplateManifest manifest)
    {
        return manifest.Parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => parameter.Default ?? parameter.Type.ToLowerInvariant() switch
            {
                "int" => ToJson(5),
                "decimal" or "number" => ToJson(1.5m),
                "bool" or "boolean" => ToJson(true),
                _ => ToJson($"value-for-{parameter.Name}")
            },
            StringComparer.Ordinal);
    }

    private static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOpts);

    private static string PolicyWithPlaceholder(string name)
        => $"<policies>\n  <inbound>\n    <set-header name=\"x-test\" exists-action=\"override\"><value>{{{{{name}}}}}</value></set-header>\n  </inbound>\n  <backend><base /></backend>\n  <outbound><base /></outbound>\n  <on-error><base /></on-error>\n</policies>";

    private static int CountOccurrences(string input, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = input.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

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

        public TemplateRoot Build()
        {
            var templateDirectory = Path.Combine(_contentRootPath, "policies", "templates", _manifest.Id);
            Directory.CreateDirectory(templateDirectory);
            File.WriteAllText(Path.Combine(templateDirectory, "template.json"), JsonSerializer.Serialize(_manifest, JsonOpts));
            File.WriteAllText(Path.Combine(templateDirectory, "policy.xml"), _policyXml);
            return new TemplateRoot(_contentRootPath);
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = nameof(TemplateRenderingTests);
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
