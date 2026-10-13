using Newtonsoft.Json.Linq;

namespace Acme.Workers;

// Workflow and task inputs arrive from the workflow engine in snake_case and are bound by ConductorSharp:
// "service_id" becomes ServiceId. These classes hold only the part of each input the workers read.

/// <summary>The part of the TMF641 order item that preparation reads.</summary>
public class OrderItem
{
    public string Action { get; set; } = "";
    public OrderedService Service { get; set; } = new();
}

public class OrderedService
{
    public List<OrderedCharacteristic> ServiceCharacteristic { get; set; } = [];
}

public class OrderedCharacteristic
{
    public string Name { get; set; } = "";
    public JToken? Value { get; set; }
}

/// <summary>What preparation decides and the later stages use.</summary>
public class AccessPlan
{
    public int Vlan { get; set; }
    public string Subnet { get; set; } = "";
    public string PeAddress { get; set; } = "";
    public string CpeAddress { get; set; } = "";
    public int BandwidthDown { get; set; }
    public int BandwidthUp { get; set; }
}
