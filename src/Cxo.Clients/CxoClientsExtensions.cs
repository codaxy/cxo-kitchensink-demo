using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceOrderingEngine.Client;
using ServiceActivationManagement.Client;
using ServiceOrderingEngine.Client;
using TmfApiClients;

namespace Cxo.Clients;

public static class CxoClientsExtensions
{
    /// <summary>
    /// Where each TMF API lives behind the gateway. The client packages take one base URL per API
    /// (<c>TmfApi:&lt;Api&gt;V4BaseUrl</c>); these are derived from <c>Cxo:GatewayUrl</c> unless set explicitly.
    /// </summary>
    private static readonly Dictionary<string, string> GatewayPaths = new()
    {
        ["PartyManagementV4BaseUrl"] = "tmf-api/party/v4/",
        ["CustomerManagementV4BaseUrl"] = "tmf-api/customerManagement/v4/",
        ["GeographicAddressManagementV4BaseUrl"] = "tmf-api/geographicAddressManagement/v4/",
        ["GeographicSiteManagementV4BaseUrl"] = "tmf-api/geographicSiteManagement/v4/",
        ["ResourceCatalogManagementV4BaseUrl"] = "tmf-api/resourceCatalog/v4/",
        ["ServiceCatalogManagementV4BaseUrl"] = "tmf-api/serviceCatalogManagement/v4/",
        ["ServiceOrderingManagementV4BaseUrl"] = "tmf-api/serviceOrdering/v4/",
        ["ServiceInventoryManagementV4BaseUrl"] = "tmf-api/serviceInventory/v4/",
        ["ResourceInventoryManagementV4BaseUrl"] = "tmf-api/resourceInventoryManagement/v4/",
        ["ResourceOrderingManagementV4BaseUrl"] = "tmf-api/resourceOrderingManagement/v4/",
        ["ServiceActivationManagementV4BaseUrl"] = "tmf-api/serviceActivationAndConfiguration/v4/",
    };

    /// <summary>
    /// Registers the CxO API clients:
    /// <list type="bullet">
    /// <item><c>TmfApiClients</c> — the TMF Open APIs (party, customer, geography, catalogs, ordering, inventories);</item>
    /// <item><c>ServiceActivationManagement.TmfApiClient</c> — TMF640 and activation workflow maps;</item>
    /// <item><c>ServiceOrderingEngineApiClient</c> — service workflow maps and order executions;</item>
    /// <item><c>ResourceOrderingEngineApiClient</c> — resource workflow maps.</item>
    /// </list>
    /// Calls to the gateway carry a client-credentials token; the engines' gRPC APIs are called directly.
    /// </summary>
    public static IHostApplicationBuilder AddCxoClients(this IHostApplicationBuilder builder)
    {
        var cxo = builder.Configuration.GetSection(CxoOptions.Section);
        var gateway = cxo["GatewayUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Set Cxo:GatewayUrl to the TMF API Gateway's address.");

        builder.Configuration.AddInMemoryCollection(GatewayPaths
            .Where(p => string.IsNullOrEmpty(builder.Configuration[$"TmfApi:{p.Key}"]))
            .Select(p => KeyValuePair.Create($"TmfApi:{p.Key}", (string?)$"{gateway}/{p.Value}")));

        builder.Services.Configure<CxoOptions>(cxo);
        builder.Services.AddSingleton<ClientCredentialsTokenSource>();
        builder.Services.AddTransient<GatewayTokenHandler>();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<GatewayTokenHandler>());

        builder.Services.AddTmfApiClients(builder.Configuration);
        builder.Services.AddServiceActivationTmfApiClient(builder.Configuration);
        builder.Services.AddServiceOrderingEngineClient(builder.Configuration);
        builder.Services.AddResourceOrderingEngineClient(builder.Configuration);
        return builder;
    }
}
