namespace Cxo.Clients;

/// <summary>Where CxO is, and which Keycloak client calls it. Bound from the <c>Cxo</c> configuration section.</summary>
public sealed class CxoOptions
{
    public const string Section = "Cxo";

    /// <summary>The TMF API Gateway, e.g. <c>https://cxo.example.com</c>. Every TMF API is reached through it.</summary>
    public string GatewayUrl { get; set; } = "";

    /// <summary>
    /// Keycloak's token endpoint. Must be the same Keycloak address the gateway trusts,
    /// or the gateway refuses the token with 401.
    /// </summary>
    public string TokenUrl { get; set; } = "";

    /// <summary>A confidential Keycloak client with service accounts and the gateway roles it needs.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Pass it through the environment (<c>Cxo__ClientSecret</c>), not the settings file.</summary>
    public string ClientSecret { get; set; } = "";
}
