using ConductorSharp.Engine.Builders;
using ConductorSharp.Engine.Builders.Metadata;

namespace Acme.Workers;

// Workflow names must match the workflow maps (Acme.Names.Workflows); the CLI's setup maps them.

// The service ordering engine starts each stage workflow with order_id, item_id and service_id;
// preparation also receives the order item, and the later stages receive preparation's output.

public class StageInput<TOutput> : WorkflowInput<TOutput> where TOutput : WorkflowOutput
{
    public string OrderId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public OrderItem? Item { get; set; }
    public AccessPlan? PrepareOutput { get; set; }
}

public class NoOutput : WorkflowOutput { }

public class PlanOutput : WorkflowOutput
{
    public int Vlan { get; set; }
    public string Subnet { get; set; } = "";
    public string PeAddress { get; set; } = "";
    public string CpeAddress { get; set; } = "";
    public int BandwidthDown { get; set; }
    public int BandwidthUp { get; set; }
}

/// <summary>Preparation for the Internet service: plan the VLAN and subnet.</summary>
[OriginalName("acme_internet_prepare")]
public class InternetPrepare(WorkflowDefinitionBuilder<InternetPrepare, StageInput<PlanOutput>, PlanOutput> builder)
    : Workflow<InternetPrepare, StageInput<PlanOutput>, PlanOutput>(builder)
{
    public PlanAccess.Handler Plan { get; set; } = null!;

    public override void BuildDefinition()
    {
        _builder.AddTask(wf => wf.Plan, wf => new PlanAccess { ServiceId = wf.WorkflowInput.ServiceId, Item = wf.WorkflowInput.Item! });
        _builder.SetOutput(wf => new PlanOutput
        {
            Vlan = wf.Plan.Output.Vlan,
            Subnet = wf.Plan.Output.Subnet,
            PeAddress = wf.Plan.Output.PeAddress,
            CpeAddress = wf.Plan.Output.CpeAddress,
            BandwidthDown = wf.Plan.Output.BandwidthDown,
            BandwidthUp = wf.Plan.Output.BandwidthUp,
        });
    }
}

/// <summary>Feasibility check for the Internet service.</summary>
[OriginalName("acme_internet_check")]
public class InternetCheck(WorkflowDefinitionBuilder<InternetCheck, StageInput<NoOutput>, NoOutput> builder)
    : Workflow<InternetCheck, StageInput<NoOutput>, NoOutput>(builder)
{
    public CheckAccess.Handler Check { get; set; } = null!;

    public override void BuildDefinition() =>
        _builder.AddTask(wf => wf.Check, wf => new CheckAccess { Plan = wf.WorkflowInput.PrepareOutput! });
}

/// <summary>Fulfillment for the Internet service: configure the devices, then record the resources.</summary>
[OriginalName("acme_internet_fulfill")]
public class InternetFulfill(WorkflowDefinitionBuilder<InternetFulfill, StageInput<NoOutput>, NoOutput> builder)
    : Workflow<InternetFulfill, StageInput<NoOutput>, NoOutput>(builder)
{
    public ConfigureNetwork.Handler Configure { get; set; } = null!;
    public RecordResources.Handler Record { get; set; } = null!;

    public override void BuildDefinition()
    {
        _builder.AddTask(wf => wf.Configure, wf => new ConfigureNetwork { Plan = wf.WorkflowInput.PrepareOutput! });
        _builder.AddTask(wf => wf.Record, wf => new RecordResources { ServiceId = wf.WorkflowInput.ServiceId, Plan = wf.WorkflowInput.PrepareOutput! });
    }
}

/// <summary>Fulfillment for the CPE.</summary>
[OriginalName("acme_cpe_fulfill")]
public class CpeFulfill(WorkflowDefinitionBuilder<CpeFulfill, StageInput<NoOutput>, NoOutput> builder)
    : Workflow<CpeFulfill, StageInput<NoOutput>, NoOutput>(builder)
{
    public RegisterCpe.Handler Register { get; set; } = null!;

    public override void BuildDefinition() =>
        _builder.AddTask(wf => wf.Register, wf => new RegisterCpe { ServiceId = wf.WorkflowInput.ServiceId });
}

/// <summary>Removes a service's resources: the fulfillment rollback for Add, and the fulfillment for Delete.</summary>
[OriginalName("acme_release")]
public class Release(WorkflowDefinitionBuilder<Release, StageInput<NoOutput>, NoOutput> builder)
    : Workflow<Release, StageInput<NoOutput>, NoOutput>(builder)
{
    public ReleaseResources.Handler ReleaseAll { get; set; } = null!;

    public override void BuildDefinition() =>
        _builder.AddTask(wf => wf.ReleaseAll, wf => new ReleaseResources { ServiceId = wf.WorkflowInput.ServiceId });
}

/// <summary>Activation workflow for TMF640: started with service_id, monitor_id and the requested state.</summary>
public class ActivationInput : WorkflowInput<NoOutput>
{
    public string ServiceId { get; set; } = "";
    public string MonitorId { get; set; } = "";
    public string? State { get; set; }
}

[OriginalName("acme_activate")]
public class Activate(WorkflowDefinitionBuilder<Activate, ActivationInput, NoOutput> builder)
    : Workflow<Activate, ActivationInput, NoOutput>(builder)
{
    public SetAdminState.Handler Apply { get; set; } = null!;

    public override void BuildDefinition() =>
        _builder.AddTask(wf => wf.Apply, wf => new SetAdminState { ServiceId = wf.WorkflowInput.ServiceId, State = wf.WorkflowInput.State });
}
