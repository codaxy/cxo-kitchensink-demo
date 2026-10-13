using ConductorSharp.Engine;
using ConductorSharp.Engine.Builders.Metadata;
using ConductorSharp.Engine.Exceptions;
using MediatR;
using Microsoft.Extensions.Logging;
using TmfApiClients.ResourceInventoryManagement.v4;
using static AcmeVpn.Names;

namespace AcmeVpn.Workers;

// Resource workflows: the resource ordering engine creates each resource of an add item before preparation, then runs
// these with its resource_id. They work on that one resource.

/// <summary>Refuses a site attachment whose CPE has not been installed yet.</summary>
public class CheckCpe : IRequest<CheckCpe.Result>
{
    public string ResourceId { get; set; } = "";

    public class Result { public string CpeResourceId { get; set; } = ""; }

    [OriginalName("acme_vpn_check_cpe_installed")]
    public class Handler(IResourceInventoryManagement4ApiClient resources) : TaskRequestHandler<CheckCpe, Result>
    {
        public override async Task<Result> Handle(CheckCpe request, CancellationToken cancellationToken)
        {
            var attachment = await resources.GetResourceAsync(request.ResourceId, cancellationToken);
            var serial = attachment.ResourceCharacteristic.Find("cpeSerialNumber")!.GetValue<string>();

            var cpe = (await resources.ListResourcesAsync(new() { Name = $"CPE {serial}" }, cancellationToken)).Items.FirstOrDefault()
                ?? throw new StructuredErrorException(
                    CpeNotInstalledError,
                    $"No CPE with serial number {serial} is installed at the site yet.");
            return new Result { CpeResourceId = cpe.Id! };
        }
    }
}

/// <summary>
/// Stands in for configuring the device the resource lives on: renders its configuration, then marks the resource
/// available. A real worker would call the device's adapter here.
/// </summary>
public class Configure : IRequest<Configure.Result>
{
    public string ResourceId { get; set; } = "";

    public class Result { public string Configuration { get; set; } = ""; }

    [OriginalName("acme_vpn_configure_resource")]
    public class Handler(IResourceInventoryManagement4ApiClient resources, ILogger<Handler> log) : TaskRequestHandler<Configure, Result>
    {
        public override async Task<Result> Handle(Configure request, CancellationToken cancellationToken)
        {
            var resource = await resources.GetResourceAsync(request.ResourceId, cancellationToken);
            var c = resource.ResourceCharacteristic;
            var configuration = resource.ResourceSpecification?.Name switch
            {
                ResourceSpecs.Instance =>
                    $"vrf ACME-{c.Find("vpnId")} rd {c.Find("routeTarget")} route-target both {c.Find("routeTarget")}",
                ResourceSpecs.Attachment =>
                    $"{c.Find("peRouter")}: interface {c.Find("peInterface")} vrf {c.Find("routeTarget")} address {c.Find("peAddress")} rate {c.Find("bandwidth")} Mbps",
                ResourceSpecs.CpeInterface =>
                    $"CPE: interface {c.Find("interfaceName")} encapsulation dot1q {c.Find("vlanId")} address {c.Find("address")}",
                var other => throw new InvalidOperationException($"No configuration for resource specification {other}."),
            };
            log.LogInformation("Configured {Resource}: {Configuration}", resource.Name, configuration);

            await resources.MergePatchResourceAsync(resource.Id!, new LogicalResource { ResourceStatus = ResourceStatusType.Available }, cancellationToken);
            return new Result { Configuration = configuration };
        }
    }
}

/// <summary>Stands in for removing the resource's configuration from its device. The engine then suspends the resource.</summary>
public class Unconfigure : IRequest<Unconfigure.Result>
{
    public string ResourceId { get; set; } = "";

    public class Result { public string Removed { get; set; } = ""; }

    [OriginalName("acme_vpn_unconfigure_resource")]
    public class Handler(IResourceInventoryManagement4ApiClient resources, ILogger<Handler> log) : TaskRequestHandler<Unconfigure, Result>
    {
        public override async Task<Result> Handle(Unconfigure request, CancellationToken cancellationToken)
        {
            var resource = await resources.GetResourceAsync(request.ResourceId, cancellationToken);
            log.LogInformation("Removed the configuration of {Resource}", resource.Name);
            return new Result { Removed = resource.Name ?? resource.Id! };
        }
    }
}
