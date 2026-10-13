using System.Text.Json.Nodes;
using TmfApiClients.ResourceInventoryManagement.v4;
using TmfApiClients.ServiceOrderingManagement.v4;
using static AcmeVpn.Names;
using ServiceStateType = TmfApiClients.ServiceInventoryManagement.v4.ServiceStateType;

namespace AcmeVpn.Cli;

/// <summary>
/// Runs the whole VPN example and checks each outcome: the partial order, the resource orders behind it, the fix and
/// the retry, the inventory, and the removal. Exit code 1 if anything differs. AcmeVpn.Workers must be running.
/// </summary>
public class Walkthrough(Setup setup, Orders orders, Execution execution, Inventory inventory)
{
    private readonly List<string> _failures = [];

    public async Task<int> Run(CancellationToken ct)
    {
        var state = State.Load();
        var south = Sites.First(s => s.Key == "south");

        Title("Setup: customer, three sites, catalog, workflow maps — and Branch South's CPE not installed yet");
        await setup.Run(state, ct);
        await setup.UninstallCpe(south.CpeSerial, ct);

        Title("One order for the VPN and its three sites");
        var order = await orders.Follow((await orders.Place(Orders.VpnOrder(state), ct)).Id!, ct);
        state.Order = order.Id;
        state.VpnService = Item(order, "vpn").Service!.Id;
        state.SiteServices = Sites.ToDictionary(s => s.Key, s => Item(order, s.Key).Service!.Id!);
        state.Save();
        Check(order.State == ServiceOrderStateType.Partial, $"order partial ({order.State})");
        Check(Item(order, "vpn").State == ServiceOrderItemStateType.Completed, "the VPN completed first");
        Check(Item(order, "hq").State == ServiceOrderItemStateType.Completed && Item(order, "north").State == ServiceOrderItemStateType.Completed,
            "HQ and Branch North completed");
        var error = Item(order, "south").ErrorMessage?.FirstOrDefault();
        Check(Item(order, "south").State == ServiceOrderItemStateType.Failed && error?.Code == CpeNotInstalledError,
            $"Branch South failed with {CpeNotInstalledError}, carried from its resource order ({error?.Code})");
        Check(error?.ReferenceError?.StartsWith("/tmf-api/resourceOrderingManagement/v4/resourceOrder/") == true,
            "the error points to the resource order");

        Title("The order execution: every item's resource order");
        var drilldown = await execution.Drilldown(order.Id!, ct);
        Execution.Print(drilldown);
        var southOrders = ResourceOrders(drilldown, "south");
        Check(new[] { "vpn", "hq", "north", "south" }.All(key => ResourceOrders(drilldown, key).Count == 1), "one linked resource order per item");
        Check((string?)southOrders.FirstOrDefault()?["state"] == "failed", "Branch South's resource order failed");
        var southResourceOrder = (string?)southOrders.FirstOrDefault()?["resourceOrderId"];

        Title("Install Branch South's CPE and retry the order");
        await setup.InstallCpe(state, south.Key, south.CpeSerial, await setup.CpeDeviceSpec(ct), ct);
        await orders.Retry(order.Id!, ct);
        order = await orders.Follow(order.Id!, ct);
        Check(order.State == ServiceOrderStateType.Completed, $"order completed after the retry ({order.State})");
        drilldown = await execution.Drilldown(order.Id!, ct);
        southOrders = ResourceOrders(drilldown, "south");
        Check(southOrders.Count == 1 && (string?)southOrders[0]?["resourceOrderId"] == southResourceOrder && (string?)southOrders[0]?["state"] == "completed",
            "Branch South's resource order was retried, not created again");
        Check((string?)ResourceOrders(drilldown, "hq").FirstOrDefault()?["state"] == "completed", "HQ was not repeated");

        if (order.State != ServiceOrderStateType.Completed)
            return Done("The order did not complete, so the inventory and removal steps cannot run.");

        Title("Inventory");
        await inventory.Print(state, ct);
        var vpn = await inventory.Service(state.VpnService!, ct);
        var instance = (await inventory.Resources(vpn, ct)).SingleOrDefault();
        Check(vpn.State == ServiceStateType.Active && instance?.ResourceStatus == ResourceStatusType.Available, "VPN active on an available VPN instance");
        foreach (var site in Sites)
        {
            var service = await inventory.Service(state.SiteServices[site.Key], ct);
            var linked = await inventory.Resources(service, ct);
            Check(service.State == ServiceStateType.Active && linked.Count == 2 && linked.All(r => r.ResourceStatus == ResourceStatusType.Available),
                $"{site.Name}: active, attachment and CPE interface available");
            Check(linked.All(r => r.ResourceRelationship?.Any(rel => rel.RelationshipType == "isPartOf" && rel.Resource?.Id == instance?.Id) == true),
                $"{site.Name}: its resources are part of the VPN instance");
        }

        Title("Remove the VPN: the sites first, then the VPN");
        var sites = new Dictionary<string, TmfApiClients.ServiceInventoryManagement.v4.Service>();
        foreach (var site in Sites) sites[site.Key] = await inventory.Service(state.SiteServices[site.Key], ct);
        var removal = await orders.Follow((await orders.Place(Orders.RemovalOrder(vpn, sites), ct)).Id!, ct);
        Check(removal.State == ServiceOrderStateType.Completed, $"removal completed ({removal.State})");
        foreach (var id in state.SiteServices.Values.Append(state.VpnService!))
        {
            var service = await inventory.Service(id, ct);
            Check(service.State == ServiceStateType.Terminated && (await inventory.Resources(service, ct)).All(r => r.ResourceStatus == ResourceStatusType.Suspended),
                $"{service.Name}: terminated, its resources suspended");
        }

        return Done();
    }

    private int Done(string? stoppedBecause = null)
    {
        Console.WriteLine();
        if (stoppedBecause is not null) Console.WriteLine(stoppedBecause);
        Console.WriteLine(_failures.Count == 0 ? "All checks passed." : $"{_failures.Count} check(s) failed:\n  " + string.Join("\n  ", _failures));
        return _failures.Count == 0 ? 0 : 1;
    }

    private static ServiceOrderItem Item(ServiceOrder order, string id) => order.ServiceOrderItem!.First(i => i.Id == id);

    private static JsonArray ResourceOrders(JsonObject drilldown, string itemId) =>
        drilldown["items"]!.AsArray().First(i => (string?)i!["serviceOrderItemId"] == itemId)!["resourceOrders"]!.AsArray();

    private void Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}");
        if (!ok) _failures.Add(what);
    }

    private static void Title(string text) => Console.WriteLine($"\n{text}");
}
