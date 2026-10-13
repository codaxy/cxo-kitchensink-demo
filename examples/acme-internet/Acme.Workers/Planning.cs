using ConductorSharp.Engine;
using ConductorSharp.Engine.Builders.Metadata;
using ConductorSharp.Engine.Exceptions;
using MediatR;
using TmfApiClients.ResourceInventoryManagement.v4;
using TmfApiClients.ServiceInventoryManagement.v4;
using static Acme.Names;

namespace Acme.Workers;

/// <summary>Reads the ordered speeds and plans the VLAN and subnet. Reuses the plan of a service that already has one.</summary>
public class PlanAccess : IRequest<AccessPlan>
{
    public string ServiceId { get; set; } = "";
    public OrderItem Item { get; set; } = new();

    [OriginalName("acme_plan_access")]
    public class Handler(IServiceInventoryManagement4ApiClient services, IResourceInventoryManagement4ApiClient resources)
        : TaskRequestHandler<PlanAccess, AccessPlan>
    {
        public override async Task<AccessPlan> Handle(PlanAccess request, CancellationToken cancellationToken)
        {
            var service = await services.GetServiceAsync(request.ServiceId, cancellationToken);

            // A speed the order leaves out keeps the service's current value.
            int Speed(string name) =>
                request.Item.Service.ServiceCharacteristic.FirstOrDefault(c => c.Name == name)?.Value?.ToObject<int>()
                ?? service.ServiceCharacteristic!.First(c => c.Name == name).Value!.GetValue<int>();

            var vlan = await ExistingVlan(service, cancellationToken) ?? await NextFreeVlan(cancellationToken);
            var block = vlan - 2249;
            return new AccessPlan
            {
                Vlan = vlan,
                Subnet = $"1.1.{block}.0/30",
                PeAddress = $"1.1.{block}.1",
                CpeAddress = $"1.1.{block}.2",
                BandwidthDown = Speed(Characteristics.BandwidthDown),
                BandwidthUp = Speed(Characteristics.BandwidthUp),
            };
        }

        private async Task<int?> ExistingVlan(Service service, CancellationToken cancellationToken)
        {
            foreach (var root in service.SupportingResource ?? [])
            {
                var parts = await resources.ListResourcesAsync(new() { RelatedResourceId = root.Id }, cancellationToken);
                var vlan = parts.Items.SelectMany(p => p.ResourceCharacteristic ?? []).Find("vlan");
                if (vlan is not null) return vlan.GetValue<int>();
            }
            return null;
        }

        private async Task<int> NextFreeVlan(CancellationToken cancellationToken)
        {
            var bearers = await resources.ListResourcesAsync(new() { ResourceCharacteristicName = "vlan", Limit = 1000 }, cancellationToken);
            var used = bearers.Items
                .SelectMany(b => b.ResourceCharacteristic ?? [])
                .Where(c => c.Name == "vlan")
                .Select(c => c.Value!.GetValue<int>());
            return used.DefaultIfEmpty(2249).Max() + 1;
        }
    }
}

/// <summary>Refuses speeds the access port cannot carry.</summary>
public class CheckAccess : IRequest<CheckAccess.Result>
{
    public AccessPlan Plan { get; set; } = new();

    public class Result { public bool Feasible { get; set; } }

    private const int AccessPortMbps = 1000;

    [OriginalName("acme_check_access")]
    public class Handler : TaskRequestHandler<CheckAccess, Result>
    {
        public override Task<Result> Handle(CheckAccess request, CancellationToken cancellationToken)
        {
            var needed = Math.Max(request.Plan.BandwidthDown, request.Plan.BandwidthUp);

            if (needed > AccessPortMbps)
                throw new StructuredErrorException(
                    "ACME_PORT_CAPACITY",
                    $"The access port carries at most {AccessPortMbps} Mbps; {needed} Mbps was ordered.");
            return Task.FromResult(new Result { Feasible = true });
        }
    }
}
