using ConductorSharp.Engine;
using ConductorSharp.Engine.Builders.Metadata;
using MediatR;
using ServiceOrderingEngine.Extensions;
using TmfApiClients.GeographicSiteManagement.v4;
using TmfApiClients.ResourceInventoryManagement.v4;
using TmfApiClients.ResourceOrderingManagement.v4;
using TmfApiClients.ServiceInventoryManagement.v4;
using static AcmeVpn.Names;
using Characteristic = TmfApiClients.ResourceOrderingManagement.v4.Characteristic;
using RoResourceRef = TmfApiClients.ResourceOrderingManagement.v4.ResourceRefOrValue;
using ServiceStateType = TmfApiClients.ServiceInventoryManagement.v4.ServiceStateType;

namespace AcmeVpn.Workers;

// Service workflows hand the network work to resource orders: preparation plans, fulfillment orders the resources,
// waits for the resource order, and links what it created to the service.

/// <summary>What the VPN's preparation decides.</summary>
public class VpnPlan
{
    public int VpnId { get; set; }
    public string RouteTarget { get; set; } = "";
}

/// <summary>What a site's preparation decides.</summary>
public class SitePlan
{
    public string SiteName { get; set; } = "";
    public int VpnId { get; set; }
    public string RouteTarget { get; set; } = "";
    public string InstanceResourceId { get; set; } = "";
    public string PeRouter { get; set; } = "";
    public string PeInterface { get; set; } = "";
    public int Vlan { get; set; }
    public string PeAddress { get; set; } = "";
    public string CpeAddress { get; set; } = "";
    public int Bandwidth { get; set; }
    public string CpeSerialNumber { get; set; } = "";
}

/// <summary>A resource order the workflow is about to wait for.</summary>
public class OrderedResources
{
    public string ResourceOrderId { get; set; } = "";
    public string SignalKey { get; set; } = "";
}

/// <summary>Plans the VPN: the next free VPN id and its route target. Keeps the plan of a VPN that already has one.</summary>
public class PlanVpn : IRequest<VpnPlan>
{
    public string ServiceId { get; set; } = "";

    [OriginalName("acme_vpn_plan")]
    public class Handler(IServiceInventoryManagement4ApiClient services, IResourceInventoryManagement4ApiClient resources)
        : TaskRequestHandler<PlanVpn, VpnPlan>
    {
        public override async Task<VpnPlan> Handle(PlanVpn request, CancellationToken cancellationToken)
        {
            var service = await services.GetServiceAsync(request.ServiceId, cancellationToken);
            if (service.SupportingResource?.FirstOrDefault() is { } existing)
            {
                var instance = await resources.GetResourceAsync(existing.Id, cancellationToken);
                return new VpnPlan
                {
                    VpnId = instance.ResourceCharacteristic.Find("vpnId")!.GetValue<int>(),
                    RouteTarget = instance.ResourceCharacteristic.Find("routeTarget")!.GetValue<string>(),
                };
            }

            var used = (await resources.ListResourcesAsync(new() { ResourceCharacteristicName = "vpnId", Limit = 1000 }, cancellationToken)).Items
                .Select(r => r.ResourceCharacteristic.Find("vpnId")?.GetValue<int>() ?? 0);
            var vpnId = used.DefaultIfEmpty(99).Max() + 1;
            return new VpnPlan { VpnId = vpnId, RouteTarget = $"65000:{vpnId}" };
        }
    }
}

/// <summary>Plans one site of the VPN: its provider edge port, VLAN and addresses, from the VPN it belongs to.</summary>
public class PlanSite : IRequest<SitePlan>
{
    public string ServiceId { get; set; } = "";

    [OriginalName("acme_vpn_plan_site")]
    public class Handler(
        IServiceInventoryManagement4ApiClient services,
        IResourceInventoryManagement4ApiClient resources,
        IGeographicSiteManagement4ApiClient sites)
        : TaskRequestHandler<PlanSite, SitePlan>
    {
        public override async Task<SitePlan> Handle(PlanSite request, CancellationToken cancellationToken)
        {
            var site = await services.GetServiceAsync(request.ServiceId, cancellationToken);

            // The order item's depends-on relationship to the VPN item is recorded on the site's service.
            var vpnServiceId = site.ServiceRelationship?.FirstOrDefault(r => r.RelationshipType == "depends-on")?.Service?.Id
                ?? throw new InvalidOperationException($"Site service {site.Id} is not related to a VPN.");
            var vpn = await services.GetServiceAsync(vpnServiceId, cancellationToken);
            var instance = await resources.GetResourceAsync(vpn.SupportingResource!.First().Id, cancellationToken);
            var vpnId = instance.ResourceCharacteristic.Find("vpnId")!.GetValue<int>();

            var place = await sites.GetGeographicSiteAsync(site.Place!.First().Id!, cancellationToken);
            var office = Network.Office(place.Name!);
            var subnet = $"10.{vpnId % 250}.{office.Index}";

            string Text(string name) => site.ServiceCharacteristic!.First(c => c.Name == name).Value!.GetValue<string>();
            return new SitePlan
            {
                SiteName = place.Name!,
                VpnId = vpnId,
                RouteTarget = instance.ResourceCharacteristic.Find("routeTarget")!.GetValue<string>(),
                InstanceResourceId = instance.Id!,
                PeRouter = office.PeRouter,
                PeInterface = office.PeInterface,
                // The VLAN only matters on each site's own access link, so every site of a VPN can use the same one.
                Vlan = 3000 + vpnId % 1000,
                PeAddress = $"{subnet}.1",
                CpeAddress = $"{subnet}.2",
                Bandwidth = site.ServiceCharacteristic!.First(c => c.Name == Characteristics.Bandwidth).Value!.GetValue<int>(),
                CpeSerialNumber = Text(Characteristics.CpeSerialNumber),
            };
        }
    }
}

/// <summary>Orders the VPN instance — its VRF on the provider edge — as a resource order.</summary>
public class OrderVpnResources : IRequest<OrderedResources>
{
    public string OrderId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public VpnPlan Plan { get; set; } = new();

    [OriginalName("acme_vpn_order_resources")]
    public class Handler(ResourceOrders resourceOrders, ResourceSpecifications specifications)
        : TaskRequestHandler<OrderVpnResources, OrderedResources>
    {
        public override async Task<OrderedResources> Handle(OrderVpnResources request, CancellationToken cancellationToken)
        {
            var p = request.Plan;
            var spec = await specifications.Id(ResourceSpecs.Instance, cancellationToken);
            var order = await resourceOrders.Ensure(request.OrderId, request.ItemId, () => ResourceOrderBuilder.Create()
                .WithName($"VPN {p.VpnId}")
                .AddItem("instance", new RoResourceRef
                {
                    Type = "LogicalResource",
                    Name = $"VPN {p.VpnId}",
                    ResourceSpecification = new() { Id = spec, Name = ResourceSpecs.Instance },
                    ResourceCharacteristic = [Pair("vpnId", p.VpnId), Pair("routeTarget", p.RouteTarget)],
                })
                , cancellationToken);
            return new OrderedResources { ResourceOrderId = order.Id!, SignalKey = ResourceOrders.SignalKey(order.Id!) };
        }
    }

    internal static Characteristic Pair(string name, object value) =>
        ResourceCharacteristics.Create(name, value);
}

/// <summary>
/// Orders a site's resources: its attachment to the VPN on the provider edge, and the CPE interface, which
/// depends on the attachment. Both belong to the VPN instance.
/// </summary>
public class OrderSiteResources : IRequest<OrderedResources>
{
    public string OrderId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public SitePlan Plan { get; set; } = new();

    [OriginalName("acme_vpn_order_site_resources")]
    public class Handler(ResourceOrders resourceOrders, ResourceSpecifications specifications)
        : TaskRequestHandler<OrderSiteResources, OrderedResources>
    {
        public override async Task<OrderedResources> Handle(OrderSiteResources request, CancellationToken cancellationToken)
        {
            var p = request.Plan;
            var attachment = await specifications.Id(ResourceSpecs.Attachment, cancellationToken);
            var cpeInterface = await specifications.Id(ResourceSpecs.CpeInterface, cancellationToken);
            var partOfVpn = new TmfApiClients.ResourceOrderingManagement.v4.ResourceRelationship
            {
                RelationshipType = "isPartOf",
                Resource = new() { Id = p.InstanceResourceId },
            };

            var order = await resourceOrders.Ensure(request.OrderId, request.ItemId, () => ResourceOrderBuilder.Create()
                .WithName($"{p.SiteName} on VPN {p.VpnId}")
                .AddItem("attachment", new RoResourceRef
                {
                    Type = "LogicalResource",
                    Name = $"{p.SiteName} attachment {p.PeRouter} {p.PeInterface}.{p.Vlan}",
                    ResourceSpecification = new() { Id = attachment, Name = ResourceSpecs.Attachment },
                    ResourceCharacteristic =
                    [
                        OrderVpnResources.Pair("peRouter", p.PeRouter), OrderVpnResources.Pair("peInterface", $"{p.PeInterface}.{p.Vlan}"),
                        OrderVpnResources.Pair("peAddress", $"{p.PeAddress}/30"), OrderVpnResources.Pair("routeTarget", p.RouteTarget),
                        OrderVpnResources.Pair("bandwidth", p.Bandwidth), OrderVpnResources.Pair("cpeSerialNumber", p.CpeSerialNumber),
                    ],
                    ResourceRelationship = [partOfVpn],
                })
                .AddItem("cpe-interface", new RoResourceRef
                {
                    Type = "LogicalResource",
                    Name = $"{p.SiteName} CPE {p.CpeSerialNumber} Gi0/0.{p.Vlan}",
                    ResourceSpecification = new() { Id = cpeInterface, Name = ResourceSpecs.CpeInterface },
                    ResourceCharacteristic =
                    [
                        OrderVpnResources.Pair("interfaceName", $"Gi0/0.{p.Vlan}"), OrderVpnResources.Pair("vlanId", p.Vlan),
                        OrderVpnResources.Pair("address", $"{p.CpeAddress}/30"),
                    ],
                    ResourceRelationship = [partOfVpn],
                }, item => item.OrderItemRelationship = [new() { RelationshipType = "depends-on", OrderItem = new() { ItemId = "attachment" } }])
                , cancellationToken);
            return new OrderedResources { ResourceOrderId = order.Id!, SignalKey = ResourceOrders.SignalKey(order.Id!) };
        }
    }
}

/// <summary>Orders the removal of every resource linked to a service, as a resource order with delete items.</summary>
public class OrderRemoval : IRequest<OrderedResources>
{
    public string OrderId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string ServiceId { get; set; } = "";

    [OriginalName("acme_vpn_order_removal")]
    public class Handler(IServiceInventoryManagement4ApiClient services, IResourceInventoryManagement4ApiClient resources, ResourceOrders resourceOrders)
        : TaskRequestHandler<OrderRemoval, OrderedResources>
    {
        public override async Task<OrderedResources> Handle(OrderRemoval request, CancellationToken cancellationToken)
        {
            var service = await services.GetServiceAsync(request.ServiceId, cancellationToken);
            var linked = new List<TmfApiClients.ResourceInventoryManagement.v4.Resource>();
            foreach (var reference in service.SupportingResource ?? [])
                linked.Add(await resources.GetResourceAsync(reference.Id, cancellationToken));

            var order = await resourceOrders.Ensure(request.OrderId, request.ItemId, () =>
            {
                var builder = ResourceOrderBuilder.Create().WithName($"Remove {service.Name}");
                foreach (var (resource, index) in linked.Select((r, i) => (r, i + 1)))
                    // A delete item names the resource by id and href, and carries the specification's mandatory characteristics.
                    builder.AddItem($"remove-{index}", new RoResourceRef
                    {
                        Id = resource.Id,
                        Href = $"/tmf-api/resourceInventoryManagement/v4/resource/{resource.Id}",
                        Type = resource.Type ?? "LogicalResource",
                        Name = resource.Name,
                        ResourceSpecification = new() { Id = resource.ResourceSpecification!.Id, Name = resource.ResourceSpecification.Name },
                        ResourceCharacteristic = (resource.ResourceCharacteristic ?? [])
                            .Select(c => ResourceCharacteristics.Create(c.Name!, c.Value?.DeepClone())).ToList(),
                    }, item => item.Action = TmfApiClients.ResourceOrderingManagement.v4.OrderItemActionType.Delete);
                return builder;
            }, cancellationToken);
            return new OrderedResources { ResourceOrderId = order.Id!, SignalKey = ResourceOrders.SignalKey(order.Id!) };
        }
    }
}

/// <summary>Links the resources a completed resource order created to the service, and marks the service active.</summary>
public class LinkResources : IRequest<LinkResources.Result>
{
    public string ServiceId { get; set; } = "";
    public string ResourceOrderId { get; set; } = "";

    public class Result { public int Linked { get; set; } }

    [OriginalName("acme_vpn_link_resources")]
    public class Handler(IServiceInventoryManagement4ApiClient services, IResourceOrderingManagement4ApiClient resourceOrdering)
        : TaskRequestHandler<LinkResources, Result>
    {
        public override async Task<Result> Handle(LinkResources request, CancellationToken cancellationToken)
        {
            var order = await resourceOrdering.GetResourceOrderAsync(request.ResourceOrderId, cancellationToken);
            var created = (order.OrderItem ?? []).Where(i => i.Resource?.Id is not null).Select(i => i.Resource!).ToList();

            await services.MergePatchServiceAsync(request.ServiceId, new Service_Update
            {
                State = ServiceStateType.Active,
                SupportingResource = created.Select(r => new TmfApiClients.ServiceInventoryManagement.v4.ResourceRef
                {
                    Id = r.Id!,
                    Href = $"/tmf-api/resourceInventoryManagement/v4/resource/{r.Id}",
                    Name = r.Name,
                    ReferredType = "LogicalResource",
                }).ToList(),
            }, cancellationToken);
            return new Result { Linked = created.Count };
        }
    }
}
