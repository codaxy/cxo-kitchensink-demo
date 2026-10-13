using System.Text.Json;
using ApiClientCommons.Utilities;
using TmfApiClients.ServiceOrderingManagement.v4;
using static AcmeVpn.Names;
using Characteristic = TmfApiClients.ServiceOrderingManagement.v4.Characteristic;
using Service = TmfApiClients.ServiceInventoryManagement.v4.Service;

namespace AcmeVpn.Cli;

/// <summary>The VPN's orders: one order for the VPN and its sites, retrying it, and removing it all.</summary>
public class Orders(IServiceOrderingManagement4ApiClient ordering)
{
    private static readonly ServiceOrderStateType[] FinalStates =
    [
        ServiceOrderStateType.Completed, ServiceOrderStateType.Failed, ServiceOrderStateType.Partial,
        ServiceOrderStateType.Rejected, ServiceOrderStateType.Cancelled,
    ];

    /// <summary>
    /// The VPN, and one item per office that depends on it: the engine completes the VPN first, then delivers the
    /// three sites in parallel.
    /// </summary>
    public static ServiceOrder_Create VpnOrder(State state) => new()
    {
        ExternalId = "ACME-VPN-2026-001",
        RelatedParty = [Customer(state, "Acme Corp")],
        ServiceOrderItem =
        [
            new()
            {
                Id = "vpn",
                Action = OrderItemActionType.Add,
                Service = new()
                {
                    Name = "Acme Corporate VPN",
                    ServiceSpecification = new() { Id = state.VpnSpec!, Name = ServiceSpecs.Vpn },
                    ServiceCharacteristic = [new() { Name = Characteristics.VpnName, ValueType = "string", Value = "Acme Corporate VPN" }],
                    RelatedParty = [Customer(state)],
                },
            },
            .. Sites.Select(site => new ServiceOrderItem
            {
                Id = site.Key,
                Action = OrderItemActionType.Add,
                Service = new()
                {
                    Name = $"Acme Corporate VPN — {site.Name}",
                    ServiceSpecification = new() { Id = state.SiteSpec!, Name = ServiceSpecs.Site },
                    ServiceCharacteristic =
                    [
                        new() { Name = Characteristics.Bandwidth, ValueType = "integer", Value = 1000 },
                        new() { Name = Characteristics.CpeSerialNumber, ValueType = "string", Value = site.CpeSerial },
                    ],
                    Place = [new() { Id = state.Sites[site.Key], Role = "installationAddress", ReferredType = "GeographicSite" }],
                    RelatedParty = [Customer(state)],
                },
                ServiceOrderItemRelationship = [new() { RelationshipType = "depends-on", OrderItem = new() { ItemId = "vpn" } }],
            }),
        ],
    };

    /// <summary>Removes the VPN: every site first, then the VPN, which depends on them.</summary>
    public static ServiceOrder_Create RemovalOrder(Service vpn, IReadOnlyDictionary<string, Service> sites) => new()
    {
        ServiceOrderItem =
        [
            .. sites.Select(site => new ServiceOrderItem { Id = site.Key, Action = OrderItemActionType.Delete, Service = AsOrderedService(site.Value) }),
            new()
            {
                Id = "vpn",
                Action = OrderItemActionType.Delete,
                Service = AsOrderedService(vpn),
                ServiceOrderItemRelationship = sites.Keys
                    .Select(key => new ServiceOrderItemRelationship { RelationshipType = "depends-on", OrderItem = new() { ItemId = key } }).ToList(),
            },
        ],
    };

    public async Task<ServiceOrder> Place(ServiceOrder_Create order, CancellationToken ct)
    {
        var placed = await ordering.CreateServiceOrderAsync(order, ct);
        Console.WriteLine($"  Order {placed.Id} {Describe(placed)}");
        return placed;
    }

    /// <summary>A failed or partial order resumes where it stopped: completed items and phases are not repeated.</summary>
    public async Task Retry(string orderId, CancellationToken ct)
    {
        await ordering.MergePatchServiceOrderAsync(orderId, new Partial<ServiceOrder_Update>
        {
            Data = new() { State = ServiceOrderStateType.Acknowledged },
            DefinedKeys = ["state"],
        }, ct);
        Console.WriteLine($"  Order {orderId} retried");
    }

    public async Task<ServiceOrder> Follow(string orderId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        string? last = null;
        while (true)
        {
            var order = await ordering.GetServiceOrderAsync(orderId, ct);
            var now = Describe(order);
            if (now != last) Console.WriteLine($"  {now}");
            last = now;

            if (order.State is { } s && FinalStates.Contains(s))
            {
                foreach (var item in order.ServiceOrderItem ?? [])
                foreach (var error in item.ErrorMessage ?? [])
                    Console.WriteLine($"  item {item.Id}: {error.Code} — {error.Reason}");
                return order;
            }
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Order {orderId} is still {order.State}. Is the worker application (AcmeVpn.Workers) running?");
            await Task.Delay(1000, ct);
        }
    }

    public static string Describe(ServiceOrder order) =>
        $"{Name(order.State)}: " + string.Join(", ", (order.ServiceOrderItem ?? []).Select(i => $"{i.Id} {Name(i.State)}"));

    private static string Name<T>(T? state) where T : struct, Enum =>
        state is null ? "?" : JsonNamingPolicy.CamelCase.ConvertName(state.Value.ToString());

    /// <summary>The service as stored in inventory, as an order item's service: everything a delete item must carry.</summary>
    private static ServiceRefOrValue AsOrderedService(Service current) => new()
    {
        Id = current.Id,
        Href = current.Href,
        Name = current.Name,
        State = current.State is { } state ? Enum.Parse<ServiceStateType>(state.ToString()) : null,
        ServiceSpecification = new() { Id = current.ServiceSpecification!.Id, Name = current.ServiceSpecification.Name },
        ServiceCharacteristic = (current.ServiceCharacteristic ?? [])
            .Select(c => new Characteristic { Name = c.Name, ValueType = c.ValueType, Value = c.Value!.DeepClone() }).ToList(),
        Place = (current.Place ?? []).Select(p => new RelatedPlaceRefOrValue { Id = p.Id, Role = p.Role, ReferredType = p.ReferredType }).ToList(),
        RelatedParty = (current.RelatedParty ?? [])
            .Select(p => new RelatedParty { Id = p.Id, Role = p.Role, Type = "RelatedParty", ReferredType = p.ReferredType }).ToList(),
        ServiceRelationship = (current.ServiceRelationship ?? [])
            .Select(r => new ServiceRelationship { RelationshipType = r.RelationshipType, Service = new() { Id = r.Service!.Id! } }).ToList(),
        SupportingResource = (current.SupportingResource ?? [])
            .Select(r => new ResourceRef { Id = r.Id, Href = r.Href, Name = r.Name, ReferredType = r.ReferredType }).ToList(),
    };

    private static RelatedParty Customer(State state, string? name = null) =>
        new() { Id = state.Customer!, Name = name, Role = "customer", Type = "RelatedParty", ReferredType = "Customer" };
}
