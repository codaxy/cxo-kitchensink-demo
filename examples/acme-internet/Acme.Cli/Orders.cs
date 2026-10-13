using System.Text.Json;
using TmfApiClients.ServiceOrderingManagement.v4;
using static Acme.Names;
using Characteristic = TmfApiClients.ServiceOrderingManagement.v4.Characteristic;
using Service = TmfApiClients.ServiceInventoryManagement.v4.Service;
using ServiceStateType = TmfApiClients.ServiceOrderingManagement.v4.ServiceStateType;

namespace Acme.Cli;

/// <summary>Steps 4, 5 and 7: place an order, follow it to a final state, and change or remove a service by order.</summary>
public class Orders(IServiceOrderingManagement4ApiClient ordering)
{
    private static readonly ServiceOrderStateType[] FinalStates =
    [
        ServiceOrderStateType.Completed, ServiceOrderStateType.Failed, ServiceOrderStateType.Partial,
        ServiceOrderStateType.Rejected, ServiceOrderStateType.Cancelled,
    ];

    /// <summary>
    /// Step 4: the CPE, and the Internet service that runs on it. Item 2 depends on item 1, so the engine
    /// completes the CPE first. The customer and site go on each service: inventory searches look there.
    /// </summary>
    public static ServiceOrder_Create GuideOrder(State state, int speed = 1000, string? serialNumber = "FTX1234ABCD") => new()
    {
        ExternalId = "ACME-2026-001",
        RelatedParty = [Customer(state, "Acme Corp")],
        ServiceOrderItem =
        [
            new()
            {
                Id = "1",
                Action = OrderItemActionType.Add,
                Service = new()
                {
                    Name = "Acme HQ CPE",
                    State = ServiceStateType.Active,
                    ServiceSpecification = new() { Id = state.DeviceSpec!, Name = ServiceSpecs.Device },
                    ServiceCharacteristic =
                    [
                        .. serialNumber is null ? [] : new[] { Text(Characteristics.SerialNumber, serialNumber) },
                        Text(Characteristics.Model, "ISR 1100"),
                    ],
                    Place = [Site(state)],
                    RelatedParty = [Customer(state)],
                },
            },
            new()
            {
                Id = "2",
                Action = OrderItemActionType.Add,
                Service = new()
                {
                    Name = $"Acme HQ Internet {Label(speed)}",
                    ServiceSpecification = new() { Id = state.InternetSpec!, Name = ServiceSpecs.Internet },
                    ServiceCharacteristic = Speeds(speed),
                    Place = [Site(state)],
                    RelatedParty = [Customer(state)],
                },
                ServiceOrderItemRelationship = [new() { RelationshipType = "depends-on", OrderItem = new() { ItemId = "1" } }],
            },
        ],
    };

    /// <summary>A one-item order for an Internet service on its own, e.g. to see the feasibility check refuse a speed.</summary>
    public static ServiceOrder_Create InternetOnly(State state, int speed) => new()
    {
        RelatedParty = [Customer(state)],
        ServiceOrderItem =
        [
            new()
            {
                Id = "1",
                Action = OrderItemActionType.Add,
                Service = new()
                {
                    Name = $"Acme Branch Internet {Label(speed)}",
                    ServiceSpecification = new() { Id = state.InternetSpec!, Name = ServiceSpecs.Internet },
                    ServiceCharacteristic = Speeds(speed),
                    Place = [Site(state)],
                    RelatedParty = [Customer(state)],
                },
            },
        ],
    };

    /// <summary>
    /// Step 7: a modify item carries the whole service as it should be after the change. Properties left out
    /// are cleared, and a missing state fails the update — so read the service, change it, send it all back.
    /// </summary>
    public static ServiceOrder_Create Modify(State state, Service current, int speed)
    {
        var service = AsOrderedService(current);
        service.Name = $"Acme HQ Internet {Label(speed)}";
        service.ServiceCharacteristic = Speeds(speed);
        return new()
        {
            RelatedParty = [Customer(state)],
            ServiceOrderItem = [new() { Id = "1", Action = OrderItemActionType.Modify, Service = service }],
        };
    }

    /// <summary>
    /// Step 7: a delete item is validated against its specification too, so it carries the mandatory
    /// characteristics; and like a modify, what it leaves out is cleared on the terminated service.
    /// </summary>
    public static ServiceOrder_Create Delete(Service current) => new()
    {
        ServiceOrderItem = [new() { Id = "1", Action = OrderItemActionType.Delete, Service = AsOrderedService(current) }],
    };

    public async Task<ServiceOrder> Place(ServiceOrder_Create order, CancellationToken ct)
    {
        var placed = await ordering.CreateServiceOrderAsync(order, ct);
        Console.WriteLine($"  Order {placed.Id} {Describe(placed)}");
        return placed;
    }

    /// <summary>Step 5: poll until the order is final, printing each change. Notifications can replace polling.</summary>
    public async Task<ServiceOrder> Follow(string orderId, CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
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
                throw new TimeoutException($"Order {orderId} is still {order.State}. Is the worker application running?");
            await Task.Delay(1000, ct);
        }
    }

    public static string Describe(ServiceOrder order) =>
        $"{State(order.State)}: " + string.Join(", ", (order.ServiceOrderItem ?? []).Select(i => $"item {i.Id} {State(i.State)}"));

    private static string State<T>(T? state) where T : struct, Enum =>
        state is null ? "?" : JsonNamingPolicy.CamelCase.ConvertName(state.Value.ToString());

    /// <summary>
    /// The service as stored in inventory, as an order item's service: everything Step 7 says to send back.
    /// </summary>
    private static ServiceRefOrValue AsOrderedService(Service current) => new()
    {
        Id = current.Id,
        Href = current.Href,
        Name = current.Name,
        State = current.State is { } state ? Enum.Parse<ServiceStateType>(state.ToString()) : null,
        ServiceSpecification = new() { Id = current.ServiceSpecification!.Id, Name = current.ServiceSpecification.Name },
        ServiceCharacteristic = (current.ServiceCharacteristic ?? [])
            .Select(c => new Characteristic { Name = c.Name, ValueType = c.ValueType, Value = c.Value!.DeepClone() }).ToList(),
        Place = (current.Place ?? [])
            .Select(p => new RelatedPlaceRefOrValue { Id = p.Id, Role = p.Role, ReferredType = p.ReferredType }).ToList(),
        RelatedParty = (current.RelatedParty ?? [])
            .Select(p => new RelatedParty { Id = p.Id, Role = p.Role, Type = "RelatedParty", ReferredType = p.ReferredType }).ToList(),
        // Without supportingResource the service would lose its resources, and a later delete could not find them.
        ServiceRelationship = (current.ServiceRelationship ?? [])
            .Select(r => new ServiceRelationship { RelationshipType = r.RelationshipType, Service = new() { Id = r.Service!.Id } }).ToList(),
        SupportingResource = (current.SupportingResource ?? [])
            .Select(r => new ResourceRef { Id = r.Id, Href = r.Href, Name = r.Name, ReferredType = r.ReferredType }).ToList(),
    };

    private static RelatedParty Customer(State state, string? name = null) =>
        new() { Id = state.Customer!, Name = name, Role = "customer", Type = "RelatedParty", ReferredType = "Customer" };

    private static RelatedPlaceRefOrValue Site(State state) =>
        new() { Id = state.Site, Role = "installationAddress", ReferredType = "GeographicSite" };

    private static Characteristic Text(string name, string value) => new() { Name = name, ValueType = "string", Value = value };

    private static List<Characteristic> Speeds(int mbps) =>
    [
        new() { Name = Characteristics.BandwidthDown, ValueType = "integer", Value = mbps },
        new() { Name = Characteristics.BandwidthUp, ValueType = "integer", Value = mbps },
    ];

    private static string Label(int mbps) => mbps >= 1000 ? $"{mbps / 1000}G" : $"{mbps}M";
}
