using ApiClientCommons.Exceptions;
using ApiClientCommons.Utilities;
using TmfApiClients.ServiceInventoryManagement.v4;
using TmfApiClients.ServiceOrderingManagement.v4;
using static Acme.Names;
using ServiceStateType = TmfApiClients.ServiceInventoryManagement.v4.ServiceStateType;
using SamState = ServiceActivationManagement.Client.v4.Generated.ServiceStateType;

namespace Acme.Cli;

/// <summary>
/// Runs the whole guide against a CxO environment and checks every outcome it describes:
/// the order, the inventory, the failures, the change, activation and removal. Exit code 1 if anything differs.
/// The worker application must be running.
/// </summary>
public class Walkthrough(Setup setup, Orders orders, Inventory inventory, Activation activation, IServiceOrderingManagement4ApiClient ordering)
{
    private readonly List<string> _failures = [];

    public async Task<int> Run(CancellationToken ct)
    {
        var state = State.Load();

        Title("Steps 1-3: customer, site, catalog, workflow maps");
        await setup.Run(state, ct);

        Title("Steps 4-5: place the order and follow it");
        var order = await orders.Follow((await orders.Place(Orders.GuideOrder(state), ct)).Id!, ct);
        Check(order.State == ServiceOrderStateType.Completed, $"order completed ({order.State})");
        Check(order.Milestone?.All(m => m.Status == "reached") == true, "every milestone reached");
        if (order.State != ServiceOrderStateType.Completed)
            return Done("The guide's order did not complete, so the later steps cannot run.");
        var cpeId = order.ServiceOrderItem![0].Service!.Id!;
        var internetId = order.ServiceOrderItem[1].Service!.Id!;
        state.Order = order.Id; state.CpeService = cpeId; state.InternetService = internetId; state.Save();

        Title("Step 6: inventory");
        var internet = await inventory.Service(internetId, ct);
        Check(internet.State == ServiceStateType.Active, $"Internet service active ({internet.State})");
        Check(internet.SupportingResource?.SingleOrDefault()?.Name == "Acme HQ Internet 1G access", "linked to its root resource");
        Check(internet.ServiceRelationship?.SingleOrDefault()?.RelationshipType == "depends-on", "depends on the CPE service");
        var parts = await inventory.Parts(internet, ct);
        var vlan = parts.SelectMany(p => p.ResourceCharacteristic ?? []).First(c => c.Name == "vlan").Value!.GetValue<int>();
        Check(parts.Select(p => p.Name).Order().SequenceEqual(
            new[] { $"CPE Gi1/1.{vlan}", $"PE lag-1.{vlan}", $"Switch Gi0/2.{vlan}", $"VLAN {vlan}" }.Order()), $"bearer and three interfaces on VLAN {vlan}");
        var cpe = await inventory.Service(cpeId, ct);
        Check(cpe.State == ServiceStateType.Active && cpe.SupportingResource?.SingleOrDefault()?.Name == "CPE FTX1234ABCD", "CPE service active, linked to the device");
        Check((await inventory.ByOrder(order.Id!, ct)).Length == 2, "two services found by order");
        Check((await inventory.ByCustomer(state.Customer!, ct)).Any(s => s.Id == internetId), "found by customer");
        Check((await inventory.BySite(state.Site!, ct)).Any(s => s.Id == internetId), "found by site");

        Title("Step 5: an order that breaks a catalog rule is rejected");
        var rejected = await orders.Follow((await orders.Place(Orders.GuideOrder(state, serialNumber: null), ct)).Id!, ct);
        var error = rejected.ServiceOrderItem![0].ErrorMessage?.FirstOrDefault();
        Check(rejected.State == ServiceOrderStateType.Rejected && error?.Code == "ORDER_ITEM_VALIDATION" && error.Reason?.Contains("serialNumber") == true,
            "missing serialNumber rejected with ORDER_ITEM_VALIDATION");
        Check(rejected.ServiceOrderItem[1].ErrorMessage?.FirstOrDefault()?.Reason?.Contains("serialNumber") == true, "the dependent item carries the same reason");
        var retry = await Status(() => ordering.MergePatchServiceOrderAsync(rejected.Id!,
            new Partial<ServiceOrder_Update> { Data = new() { State = ServiceOrderStateType.Acknowledged }, DefinedKeys = ["state"] }, ct));
        Check(retry == 400, $"a rejected order cannot be retried ({retry})");

        Title("Step 5: a delivery failure");
        var tooFast = await orders.Follow((await orders.Place(Orders.InternetOnly(state, 10000), ct)).Id!, ct);
        error = tooFast.ServiceOrderItem![0].ErrorMessage?.FirstOrDefault();
        Check(tooFast.State == ServiceOrderStateType.Failed && error?.Code == PortCapacityError
            && error.Reason == "The access port carries at most 1000 Mbps; 10000 Mbps was ordered.", "10 Gbps fails the feasibility check");
        var leftOver = await inventory.Service(tooFast.ServiceOrderItem[0].Service!.Id!, ct);
        Check(leftOver.State == ServiceStateType.Inactive && (leftOver.SupportingResource ?? []).Count == 0, "its service stays inactive, with no resources");

        Title("Step 7: change the speed");
        var modified = await orders.Follow((await orders.Place(Orders.Modify(state, internet, 100), ct)).Id!, ct);
        Check(modified.State == ServiceOrderStateType.Completed, $"modify completed ({modified.State})");
        internet = await inventory.Service(internetId, ct);
        Check(internet.Name == "Acme HQ Internet 100M" && internet.State == ServiceStateType.Active, "renamed and still active");
        Check(internet.ServiceCharacteristic!.All(c => c.Value!.GetValue<int>() == 100), "both speeds 100");
        Check(internet.ServiceRelationship?.Count == 1 && internet.SupportingResource?.Count == 1, "relationship and resources kept");
        var bearer = (await inventory.Parts(internet, ct)).Single(p => p.Name == $"VLAN {vlan}");
        Check(bearer.ResourceCharacteristic!.First(c => c.Name == "downloadSpeed").Value!.GetValue<int>() == 100, $"bearer updated, VLAN {vlan} kept");

        Title("Step 7: deactivate and reactivate without an order");
        foreach (var target in new[] { SamState.Inactive, SamState.Active })
        {
            var monitor = await activation.Request(internetId, target, ct);
            Check(monitor.State == "InProgress", $"TMF640 {target} accepted ({monitor.State})");
            if (target == SamState.Inactive)
            {
                var parallel = await Status(() => activation.Request(internetId, SamState.Active, ct));
                Check(parallel == 409, $"a second change while one runs is refused ({parallel})");
            }
            monitor = await activation.Follow(monitor.Id!, ct);
            var now = (await inventory.Service(internetId, ct)).State;
            Check(monitor.State == "Completed" && now?.ToString() == target.ToString(), $"monitor {monitor.State}, service {now}");
        }

        Title("Step 7: remove the service, then the CPE");
        internet = await inventory.Service(internetId, ct);
        var deleted = await orders.Follow((await orders.Place(Orders.Delete(internet), ct)).Id!, ct);
        internet = await inventory.Service(internetId, ct);
        Check(deleted.State == ServiceOrderStateType.Completed && internet.State == ServiceStateType.Terminated, "Internet service terminated");
        Check((internet.SupportingResource ?? []).Count == 0, "its resources released");
        cpe = await inventory.Service(cpeId, ct);
        deleted = await orders.Follow((await orders.Place(Orders.Delete(cpe), ct)).Id!, ct);
        Check(deleted.State == ServiceOrderStateType.Completed && (await inventory.Service(cpeId, ct)).State == ServiceStateType.Terminated, "CPE terminated");

        return Done();
    }

    private int Done(string? stoppedBecause = null)
    {
        Console.WriteLine();
        if (stoppedBecause is not null) Console.WriteLine(stoppedBecause);
        Console.WriteLine(_failures.Count == 0 ? "All checks passed." : $"{_failures.Count} check(s) failed:\n  " + string.Join("\n  ", _failures));
        return _failures.Count == 0 ? 0 : 1;
    }

    private void Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}");
        if (!ok) _failures.Add(what);
    }

    private static async Task<int> Status(Func<Task> call)
    {
        try { await call(); return 200; }
        catch (ErrorResponseException e) { return (int)e.StatusCode; }
    }

    private static void Title(string text) => Console.WriteLine($"\n{text}");
}
