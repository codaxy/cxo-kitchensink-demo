using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using TmfApiClients.ResourceInventoryManagement.v4;
using TmfApiClients.ServiceInventoryManagement.v4;

namespace AcmeVpn.Cli;

/// <summary>
/// The order execution drilldown: one call returns the service order, its items with their phases, and the resource
/// orders each item created. It is served by the service ordering engine's own HTTP API, not through the gateway.
/// </summary>
public class Execution(IHttpClientFactory httpClients, IConfiguration configuration)
{
    public async Task<JsonObject> Drilldown(string orderId, CancellationToken ct)
    {
        var url = $"{configuration["ServiceOrderingEngine:HttpUrl"]!.TrimEnd('/')}/order-execution/{orderId}/drilldown";
        return (await httpClients.CreateClient().GetFromJsonAsync<JsonObject>(url, ct))!;
    }

    public static void Print(JsonObject drilldown)
    {
        Console.WriteLine($"  service order {drilldown["serviceOrderId"]} {drilldown["state"]} (attempt {drilldown["attempt"]})");
        foreach (var item in drilldown["items"]!.AsArray())
        {
            Console.WriteLine($"  └─ {item!["serviceOrderItemId"],-6} {item["state"],-11} {item["service"]?["name"]}");
            Errors(item, "        ");
            foreach (var resourceOrder in item["resourceOrders"]!.AsArray())
                PrintResourceOrder(resourceOrder!, "     ");
        }
    }

    private static void PrintResourceOrder(JsonNode order, string indent)
    {
        Console.WriteLine($"{indent}└─ resource order {order["resourceOrderId"]} {order["state"]}");
        foreach (var item in order["items"]!.AsArray())
        {
            Console.WriteLine($"{indent}     {item!["id"],-14} {item["state"]}");
            Errors(item, indent + "       ");
        }
        foreach (var child in order["resourceOrders"]?.AsArray() ?? [])
            PrintResourceOrder(child!, indent + "   ");
    }

    private static void Errors(JsonNode node, string indent)
    {
        foreach (var error in node["errorMessage"]?.AsArray() ?? [])
            Console.WriteLine($"{indent}{error!["code"]}: {error["reason"]}");
    }
}

/// <summary>The VPN as inventory sees it: the services, and the resources the resource orders created for them.</summary>
public class Inventory(IServiceInventoryManagement4ApiClient services, IResourceInventoryManagement4ApiClient resources)
{
    public Task<Service> Service(string id, CancellationToken ct) => services.GetServiceAsync(id, ct);

    public async Task<List<Resource>> Resources(Service service, CancellationToken ct)
    {
        var found = new List<Resource>();
        foreach (var reference in service.SupportingResource ?? [])
            found.Add(await resources.GetResourceAsync(reference.Id, ct));
        return found;
    }

    public async Task Print(State state, CancellationToken ct)
    {
        foreach (var id in new[] { state.VpnService }.Concat(state.SiteServices.Values))
        {
            if (id is null) continue;
            var service = await Service(id, ct);
            Console.WriteLine($"  {service.Name} — {service.State}");
            foreach (var resource in await Resources(service, ct))
                Console.WriteLine($"    {resource.Name} ({resource.ResourceSpecification?.Name}, {resource.ResourceStatus}): "
                    + string.Join(", ", (resource.ResourceCharacteristic ?? []).Select(c => $"{c.Name}={c.Value}")));
        }
    }
}
