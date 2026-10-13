using TmfApiClients.ResourceInventoryManagement.v4;
using TmfApiClients.ServiceInventoryManagement.v4;

namespace Acme.Cli;

/// <summary>Step 6: the services an order created, and the resources the fulfillment recorded for them.</summary>
public class Inventory(IServiceInventoryManagement4ApiClient services, IResourceInventoryManagement4ApiClient resources)
{
    public Task<Service> Service(string id, CancellationToken ct) => services.GetServiceAsync(id, ct);

    /// <summary>The resources under a service's root resource: the VLAN bearer and one interface per device.</summary>
    public async Task<Resource[]> Parts(Service service, CancellationToken ct)
    {
        var root = service.SupportingResource?.FirstOrDefault();
        return root is null ? [] : (await resources.ListResourcesAsync(new() { RelatedResourceId = root.Id }, ct)).Items;
    }

    // Inventory is searchable by the same fields the order set on each service.
    public async Task<Service[]> ByCustomer(string customerId, CancellationToken ct) =>
        (await services.ListServicesAsync(new() { RelatedPartyIds = [customerId] }, ct)).Items;

    public async Task<Service[]> BySite(string siteId, CancellationToken ct) =>
        (await services.ListServicesAsync(new() { PlaceIdList = [siteId] }, ct)).Items;

    public async Task<Service[]> ByOrder(string orderId, CancellationToken ct) =>
        (await services.ListServicesAsync(new() { RelatedServiceOrderItemServiceOrderId = [orderId] }, ct)).Items;

    public async Task Print(string serviceId, CancellationToken ct)
    {
        var service = await Service(serviceId, ct);
        Console.WriteLine($"  {service.Name} ({service.ServiceSpecification?.Name}) — {service.State}");
        foreach (var c in service.ServiceCharacteristic ?? [])
            Console.WriteLine($"    {c.Name} = {c.Value}");
        foreach (var r in service.ServiceRelationship ?? [])
            Console.WriteLine($"    {r.RelationshipType} {r.Service?.Id}");
        foreach (var root in service.SupportingResource ?? [])
            Console.WriteLine($"    supporting resource: {root.Name} ({root.Id})");
        foreach (var part in await Parts(service, ct))
            Console.WriteLine($"      {part.Name}: "
                + string.Join(", ", (part.ResourceCharacteristic ?? []).Select(c => $"{c.Name}={c.Value}")));
    }
}
