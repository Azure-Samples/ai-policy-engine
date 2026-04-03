using System.Text.Json.Serialization;

namespace Chargeback.Api.Models;

/// <summary>
/// Defines what happens when no routing rule matches a request.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoutingBehavior
{
    /// <summary>Allow the request through to the originally requested deployment.</summary>
    Passthrough,

    /// <summary>Deny the request if no routing rule matches.</summary>
    Deny
}
