using ConductorSharp.Engine.Builders;
using ConductorSharp.Engine.Builders.Metadata;
using ConductorSharp.Patterns.Workflows;

namespace AcmeVpn.Workers;

// Workflow names must match the workflow maps (AcmeVpn.Names.Workflows); the CLI's setup maps them.

public class NoOutput : WorkflowOutput { }

/// <summary>What the service ordering engine starts each stage workflow with.</summary>
public class StageInput<TPlan> : WorkflowInput<NoOutput> where TPlan : class
{
    public string OrderId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public TPlan? PrepareOutput { get; set; }
}

public class VpnPlanOutput : WorkflowOutput
{
    public int VpnId { get; set; }
    public string RouteTarget { get; set; } = "";
}

public class SitePlanOutput : WorkflowOutput
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

public class PlanInput<TOutput> : WorkflowInput<TOutput> where TOutput : WorkflowOutput
{
    public string ServiceId { get; set; } = "";
}

/// <summary>Preparation for the VPN: allocate its id and route target.</summary>
[OriginalName("acme_vpn_prepare")]
public class VpnPrepare(WorkflowDefinitionBuilder<VpnPrepare, PlanInput<VpnPlanOutput>, VpnPlanOutput> builder)
    : Workflow<VpnPrepare, PlanInput<VpnPlanOutput>, VpnPlanOutput>(builder)
{
    public PlanVpn.Handler Plan { get; set; } = null!;

    public override void BuildDefinition()
    {
        _builder.AddTask(wf => wf.Plan, wf => new PlanVpn { ServiceId = wf.WorkflowInput.ServiceId });
        _builder.SetOutput(wf => new VpnPlanOutput { VpnId = wf.Plan.Output.VpnId, RouteTarget = wf.Plan.Output.RouteTarget });
    }
}

/// <summary>Fulfillment for the VPN: order its instance as a resource order, wait for it, link what it created.</summary>
[OriginalName("acme_vpn_fulfill")]
public class VpnFulfill(WorkflowDefinitionBuilder<VpnFulfill, StageInput<VpnPlan>, NoOutput> builder)
    : Workflow<VpnFulfill, StageInput<VpnPlan>, NoOutput>(builder)
{
    public OrderVpnResources.Handler OrderResources { get; set; } = null!;
    public SignalWait WaitForResources { get; set; } = null!;
    public LinkResources.Handler Link { get; set; } = null!;

    public override void BuildDefinition()
    {
        _builder.AddTask(wf => wf.OrderResources, wf => new OrderVpnResources
        {
            OrderId = wf.WorkflowInput.OrderId, ItemId = wf.WorkflowInput.ItemId, Plan = wf.WorkflowInput.PrepareOutput!,
        });
        // No worker is held while the resource order runs; the worker host signals the wait when it finishes.
        _builder.AddTask(wf => wf.WaitForResources, wf => new SignalWaitInput { SignalKey = wf.OrderResources.Output.SignalKey });
        _builder.AddTask(wf => wf.Link, wf => new LinkResources
        {
            ServiceId = wf.WorkflowInput.ServiceId, ResourceOrderId = wf.OrderResources.Output.ResourceOrderId,
        });
    }
}

/// <summary>Preparation for a site: its port, VLAN and addresses, from the VPN it joins.</summary>
[OriginalName("acme_vpn_site_prepare")]
public class SitePrepare(WorkflowDefinitionBuilder<SitePrepare, PlanInput<SitePlanOutput>, SitePlanOutput> builder)
    : Workflow<SitePrepare, PlanInput<SitePlanOutput>, SitePlanOutput>(builder)
{
    public PlanSite.Handler Plan { get; set; } = null!;

    public override void BuildDefinition()
    {
        _builder.AddTask(wf => wf.Plan, wf => new PlanSite { ServiceId = wf.WorkflowInput.ServiceId });
        _builder.SetOutput(wf => new SitePlanOutput
        {
            SiteName = wf.Plan.Output.SiteName,
            VpnId = wf.Plan.Output.VpnId,
            RouteTarget = wf.Plan.Output.RouteTarget,
            InstanceResourceId = wf.Plan.Output.InstanceResourceId,
            PeRouter = wf.Plan.Output.PeRouter,
            PeInterface = wf.Plan.Output.PeInterface,
            Vlan = wf.Plan.Output.Vlan,
            PeAddress = wf.Plan.Output.PeAddress,
            CpeAddress = wf.Plan.Output.CpeAddress,
            Bandwidth = wf.Plan.Output.Bandwidth,
            CpeSerialNumber = wf.Plan.Output.CpeSerialNumber,
        });
    }
}

/// <summary>Fulfillment for a site: order its attachment and CPE interface, wait for them, link them.</summary>
[OriginalName("acme_vpn_site_fulfill")]
public class SiteFulfill(WorkflowDefinitionBuilder<SiteFulfill, StageInput<SitePlan>, NoOutput> builder)
    : Workflow<SiteFulfill, StageInput<SitePlan>, NoOutput>(builder)
{
    public OrderSiteResources.Handler OrderResources { get; set; } = null!;
    public SignalWait WaitForResources { get; set; } = null!;
    public LinkResources.Handler Link { get; set; } = null!;

    public override void BuildDefinition()
    {
        _builder.AddTask(wf => wf.OrderResources, wf => new OrderSiteResources
        {
            OrderId = wf.WorkflowInput.OrderId, ItemId = wf.WorkflowInput.ItemId, Plan = wf.WorkflowInput.PrepareOutput!,
        });
        _builder.AddTask(wf => wf.WaitForResources, wf => new SignalWaitInput { SignalKey = wf.OrderResources.Output.SignalKey });
        _builder.AddTask(wf => wf.Link, wf => new LinkResources
        {
            ServiceId = wf.WorkflowInput.ServiceId, ResourceOrderId = wf.OrderResources.Output.ResourceOrderId,
        });
    }
}

/// <summary>Fulfillment for removing a VPN or a site: order the removal of its resources and wait for it.</summary>
[OriginalName("acme_vpn_release")]
public class VpnRelease(WorkflowDefinitionBuilder<VpnRelease, StageInput<VpnPlan>, NoOutput> builder)
    : Workflow<VpnRelease, StageInput<VpnPlan>, NoOutput>(builder)
{
    public OrderRemoval.Handler OrderRemoval { get; set; } = null!;
    public SignalWait WaitForRemoval { get; set; } = null!;

    public override void BuildDefinition()
    {
        _builder.AddTask(wf => wf.OrderRemoval, wf => new OrderRemoval
        {
            OrderId = wf.WorkflowInput.OrderId, ItemId = wf.WorkflowInput.ItemId, ServiceId = wf.WorkflowInput.ServiceId,
        });
        _builder.AddTask(wf => wf.WaitForRemoval, wf => new SignalWaitInput { SignalKey = wf.OrderRemoval.Output.SignalKey });
    }
}

/// <summary>What the resource ordering engine starts each stage workflow with.</summary>
public class ResourceStageInput : WorkflowInput<NoOutput>
{
    public string OrderId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string ResourceId { get; set; } = "";
}

/// <summary>Feasibility check for a site attachment: its CPE must be installed.</summary>
[OriginalName("acme_vpn_check_cpe")]
public class CheckCpeWorkflow(WorkflowDefinitionBuilder<CheckCpeWorkflow, ResourceStageInput, NoOutput> builder)
    : Workflow<CheckCpeWorkflow, ResourceStageInput, NoOutput>(builder)
{
    public CheckCpe.Handler Check { get; set; } = null!;

    public override void BuildDefinition() =>
        _builder.AddTask(wf => wf.Check, wf => new CheckCpe { ResourceId = wf.WorkflowInput.ResourceId });
}

/// <summary>Fulfillment for adding any of the VPN's resources: configure it on its device.</summary>
[OriginalName("acme_vpn_configure")]
public class ConfigureWorkflow(WorkflowDefinitionBuilder<ConfigureWorkflow, ResourceStageInput, NoOutput> builder)
    : Workflow<ConfigureWorkflow, ResourceStageInput, NoOutput>(builder)
{
    public Configure.Handler Configure { get; set; } = null!;

    public override void BuildDefinition() =>
        _builder.AddTask(wf => wf.Configure, wf => new Configure { ResourceId = wf.WorkflowInput.ResourceId });
}

/// <summary>Fulfillment for deleting any of the VPN's resources: remove its configuration.</summary>
[OriginalName("acme_vpn_unconfigure")]
public class UnconfigureWorkflow(WorkflowDefinitionBuilder<UnconfigureWorkflow, ResourceStageInput, NoOutput> builder)
    : Workflow<UnconfigureWorkflow, ResourceStageInput, NoOutput>(builder)
{
    public Unconfigure.Handler Unconfigure { get; set; } = null!;

    public override void BuildDefinition() =>
        _builder.AddTask(wf => wf.Unconfigure, wf => new Unconfigure { ResourceId = wf.WorkflowInput.ResourceId });
}
