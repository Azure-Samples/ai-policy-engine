namespace AIPolicyEngine.Api.Endpoints;

/// <summary>
/// Exposes runtime authentication configuration so the SPA can discover
/// which identity provider to use without baking values into the JS bundle.
/// </summary>
public static class AuthConfigEndpoints
{
    public static IEndpointRouteBuilder MapAuthConfigEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth-config", GetAuthConfig)
            .WithName("GetAuthConfig")
            .WithDescription("Returns runtime auth provider configuration for the SPA")
            .AllowAnonymous()
            .Produces<object>();

        return routes;
    }

    private static IResult GetAuthConfig(IConfiguration config)
    {
        var provider = config.GetValue<string>("AuthProvider") ?? "AzureAd";

        if (provider.Equals("Keycloak", StringComparison.OrdinalIgnoreCase))
        {
            var kc = config.GetSection("Keycloak");
            return Results.Ok(new
            {
                authProvider = "Keycloak",
                authority = kc["Authority"] ?? "",
                clientId = kc["FrontendClientId"] ?? kc["ClientId"] ?? "",
                audience = kc["Audience"] ?? "",
                realm = kc["Realm"] ?? "",
                frontendUrl = kc["FrontendUrl"] ?? "",
            });
        }

        return Results.Ok(new
        {
            authProvider = "AzureAd",
            clientId = config["AzureAd:ClientId"] ?? "",
            tenantId = config["AzureAd:TenantId"] ?? "",
            authority = config["AzureAd:Instance"] is string inst && config["AzureAd:TenantId"] is string tid
                ? $"{inst.TrimEnd('/')}/{tid}"
                : "",
            audience = config["AzureAd:Audience"] ?? "",
            scope = config["AzureAd:Audience"] is string aud && !string.IsNullOrEmpty(aud)
                ? $"api://{aud}/access_as_user"
                : "",
        });
    }
}
