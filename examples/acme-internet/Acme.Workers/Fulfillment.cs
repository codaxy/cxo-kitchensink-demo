using System.Text.Json.Nodes;
using ConductorSharp.Engine;
using ConductorSharp.Engine.Builders.Metadata;
using MediatR;
using Microsoft.Extensions.Logging;
using TmfApiClients.ResourceInventoryManagement.v4;
using TmfApiClients.ServiceInventoryManagement.v4;
using static Acme.Names;
using static Acme.Workers.CharacteristicExtensions;
using Characteristic = TmfApiClients.ResourceInventoryManagement.v4.Characteristic;
using SimResourceRef = TmfApiClients.ServiceInventoryManagement.v4.ResourceRef;

namespace Acme.Workers;

/// <summary>Stands in for configuring the devices: renders the configuration each device would receive.</summary>
public class ConfigureNetwork : IRequest<ConfigureNetwork.Result>
{
    public AccessPlan Plan { get; set; } = new();

    public class Result
    {
        public string Pe { get; set; } = "";
        public string Switch { get; set; } = "";
        public string Cpe { get; set; } = "";
    }

    [OriginalName("acme_configure_network")]
    public class Handler(ILogger<Handler> log) : TaskRequestHandler<ConfigureNetwork, Result>
    {
        public override Task<Result> Handle(ConfigureNetwork request, CancellationToken cancellationToken)
        {
            // A real worker calls a device adapter here; the example only renders the configuration.
            var p = request.Plan;
            var result = new Result
            {
                Pe = $"interface lag-1.{p.Vlan} address {p.PeAddress}/30; qos rate {p.BandwidthDown} Mbps",
                Switch = $"vlan {p.Vlan}; interface Gi1/2 and Gi0/2 switchport trunk allowed vlan add {p.Vlan}",
                Cpe = $"interface Gi1/1.{p.Vlan} encapsulation dot1q {p.Vlan}; ip address {p.CpeAddress} 255.255.255.252; rate {p.BandwidthUp} Mbps",
            };
            log.LogInformation("Configured VLAN {Vlan}: {Pe} | {Switch} | {Cpe}", p.Vlan, result.Pe, result.Switch, result.Cpe);
            return Task.FromResult(result);
        }
    }
}

/// <summary>Records the service's resources in the resource inventory, links them to the service and activates it.</summary>
public class RecordResources : IRequest<RecordResources.Result>
{
    public string ServiceId { get; set; } = "";
    public AccessPlan Plan { get; set; } = new();

    public class Result { public string RootResourceId { get; set; } = ""; }

    [OriginalName("acme_record_resources")]
    public class Handler(
        IServiceInventoryManagement4ApiClient services,
        IResourceInventoryManagement4ApiClient resources,
        ResourceSpecifications specifications)
        : TaskRequestHandler<RecordResources, Result>
    {
        public override async Task<Result> Handle(RecordResources request, CancellationToken cancellationToken)
        {
            var service = await services.GetServiceAsync(request.ServiceId, cancellationToken);
            var p = request.Plan;

            // A service that already has resources (a modify, or a retried order) only gets its speeds updated.
            var existing = service.SupportingResource?.FirstOrDefault();
            if (existing is not null)
            {
                var parts = await resources.ListResourcesAsync(new() { RelatedResourceId = existing.Id }, cancellationToken);
                var bearer = parts.Items.First(r => r.Name == $"VLAN {p.Vlan}");
                await resources.MergePatchResourceAsync(bearer.Id!, new LogicalResource { ResourceCharacteristic = Bearer(p) }, cancellationToken);
                await Activate(request.ServiceId, existing, cancellationToken);
                return new Result { RootResourceId = existing.Id };
            }

            var root = await Create($"{service.Name} access", ResourceSpecs.Root, [], null, cancellationToken);
            await Create($"VLAN {p.Vlan}", ResourceSpecs.Bearer, Bearer(p), root, cancellationToken);
            await Create($"PE lag-1.{p.Vlan}", ResourceSpecs.Interface, Interface($"lag-1.{p.Vlan}", p.Vlan, $"{p.PeAddress}/30"), root, cancellationToken);
            await Create($"Switch Gi0/2.{p.Vlan}", ResourceSpecs.Interface, Interface("Gi0/2", p.Vlan, "Access trunk to CPE"), root, cancellationToken);
            await Create($"CPE Gi1/1.{p.Vlan}", ResourceSpecs.Interface, Interface($"Gi1/1.{p.Vlan}", p.Vlan, $"{p.CpeAddress}/30"), root, cancellationToken);

            var rootRef = new SimResourceRef { Id = root.Id!, Href = root.Href, Name = root.Name, ReferredType = "LogicalResource" };
            await Activate(request.ServiceId, rootRef, cancellationToken);
            return new Result { RootResourceId = root.Id! };
        }

        // Marks the service active and links it to the root of its resources.
        private Task Activate(string serviceId, SimResourceRef root, CancellationToken cancellationToken) =>
            services.MergePatchServiceAsync(serviceId, new Service_Update
            {
                State = ServiceStateType.Active,
                SupportingResource = [new() { Id = root.Id, Href = root.Href, Name = root.Name, ReferredType = "LogicalResource" }],
            }, cancellationToken);

        private async Task<Resource> Create(string name, string specification, List<Characteristic> characteristics, Resource? partOf, CancellationToken cancellationToken)
        {
            var resource = new LogicalResource
            {
                Name = name,
                ResourceSpecification = await specifications.Ref(specification, cancellationToken),
                ResourceCharacteristic = characteristics,
            };
            if (partOf is not null)
                resource.ResourceRelationship = [new() { RelationshipType = "isPartOf", Resource = new() { Id = partOf.Id } }];
            return await resources.CreateResourceAsync(resource, cancellationToken);
        }

        private static List<Characteristic> Bearer(AccessPlan p) =>
        [
            Pair("vlan", p.Vlan), Pair("downloadSpeed", p.BandwidthDown), Pair("uploadSpeed", p.BandwidthUp), Pair("accessId", $"ACME-{p.Vlan}"),
        ];

        private static List<Characteristic> Interface(string interfaceName, int vlan, string description) =>
        [
            Pair("interfaceName", interfaceName), Pair("vlanId", vlan.ToString()), Pair("interfaceDescription", description),
        ];
    }
}

/// <summary>Records the installed CPE as a physical resource and activates the device service.</summary>
public class RegisterCpe : IRequest<RegisterCpe.Result>
{
    public string ServiceId { get; set; } = "";

    public class Result { public string ResourceId { get; set; } = ""; }

    [OriginalName("acme_register_cpe")]
    public class Handler(
        IServiceInventoryManagement4ApiClient services,
        IResourceInventoryManagement4ApiClient resources,
        ResourceSpecifications specifications)
        : TaskRequestHandler<RegisterCpe, Result>
    {
        public override async Task<Result> Handle(RegisterCpe request, CancellationToken cancellationToken)
        {
            var service = await services.GetServiceAsync(request.ServiceId, cancellationToken);
            if (service.SupportingResource?.FirstOrDefault() is { } existing)
                return new Result { ResourceId = existing.Id };

            string Value(string name) => service.ServiceCharacteristic!.First(c => c.Name == name).Value!.GetValue<string>();
            var cpe = await resources.CreateResourceAsync(new PhysicalResource
            {
                Name = $"CPE {Value(Characteristics.SerialNumber)}",
                SerialNumber = Value(Characteristics.SerialNumber),
                ResourceSpecification = await specifications.Ref(ResourceSpecs.Cpe, cancellationToken),
                ResourceCharacteristic = [Pair("model", Value(Characteristics.Model))],
            }, cancellationToken);

            await services.MergePatchServiceAsync(request.ServiceId, new Service_Update
            {
                State = ServiceStateType.Active,
                SupportingResource = [new() { Id = cpe.Id!, Href = cpe.Href, Name = cpe.Name, ReferredType = "PhysicalResource" }],
            }, cancellationToken);
            return new Result { ResourceId = cpe.Id! };
        }
    }
}

/// <summary>Removes the resources linked to a service. Safe to run twice and on a half-built service.</summary>
public class ReleaseResources : IRequest<ReleaseResources.Result>
{
    public string ServiceId { get; set; } = "";

    public class Result { public int Removed { get; set; } }

    [OriginalName("acme_release_resources")]
    public class Handler(IServiceInventoryManagement4ApiClient services, IResourceInventoryManagement4ApiClient resources)
        : TaskRequestHandler<ReleaseResources, Result>
    {
        public override async Task<Result> Handle(ReleaseResources request, CancellationToken cancellationToken)
        {
            var service = await services.GetServiceAsync(request.ServiceId, cancellationToken);
            var removed = 0;
            foreach (var root in service.SupportingResource ?? [])
            {
                var parts = await resources.ListResourcesAsync(new() { RelatedResourceId = root.Id }, cancellationToken);
                foreach (var part in parts.Items)
                {
                    await resources.DeleteResourceAsync(part.Id!, cancellationToken);
                    removed++;
                }
                await resources.DeleteResourceAsync(root.Id, cancellationToken);
                removed++;
            }
            await services.MergePatchServiceAsync(request.ServiceId, new Service_Update { SupportingResource = [] }, cancellationToken);
            return new Result { Removed = removed };
        }
    }
}

/// <summary>Stands in for enabling or shutting down the customer interface. Used by the activation workflow.</summary>
public class SetAdminState : IRequest<SetAdminState.Result>
{
    public string ServiceId { get; set; } = "";
    public string? State { get; set; }

    public class Result { public string Command { get; set; } = ""; }

    [OriginalName("acme_set_admin_state")]
    public class Handler(ILogger<Handler> log) : TaskRequestHandler<SetAdminState, Result>
    {
        public override Task<Result> Handle(SetAdminState request, CancellationToken cancellationToken)
        {
            var command = request.State == "inactive" ? "shutdown" : "no shutdown";
            log.LogInformation("Service {ServiceId}: {Command}", request.ServiceId, command);
            return Task.FromResult(new Result { Command = command });
        }
    }
}
