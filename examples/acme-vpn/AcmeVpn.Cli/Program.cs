using AcmeVpn.Cli;
using Cxo.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static AcmeVpn.Names;

const string Usage = """
    Works through the Acme VPN example: one order for a VPN and three sites, delivered through resource orders.

      vpn setup              Customer and sites, catalog, workflow maps; installs the CPEs at HQ and Branch North
      vpn order              Order the VPN and its sites, and follow the order — Branch South fails: no CPE yet
      vpn execution          The order's execution: each item, and the resource order it created
      vpn install-cpe south  Register Branch South's CPE
      vpn retry              Retry the order; it resumes where it stopped
      vpn show               The VPN, its sites, and their resources
      vpn delete             Remove the sites and the VPN
      vpn walkthrough        All of the above, with a check for each outcome

    Start the worker application (AcmeVpn.Workers) first; orders wait for it.
    """;

// Settings come from appsettings.json next to the binary, overridden by environment variables (Cxo__ClientSecret).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.AddCxoClients();
builder.Services.AddTransient<Setup>().AddTransient<Orders>().AddTransient<Execution>()
    .AddTransient<Inventory>().AddTransient<Walkthrough>();
using var host = builder.Build();
var sp = host.Services;
var ct = CancellationToken.None;

var state = State.Load();
// Configuration problems end in one line saying what to check, not a stack trace.
try
{
    switch (args.FirstOrDefault())
    {
        case "setup":
            await sp.GetRequiredService<Setup>().Run(state, ct);
            break;

        case "order":
        {
            var orders = sp.GetRequiredService<Orders>();
            var order = await orders.Follow((await orders.Place(Orders.VpnOrder(state), ct)).Id!, ct);
            state.Order = order.Id;
            state.VpnService = order.ServiceOrderItem!.First(i => i.Id == "vpn").Service!.Id;
            state.SiteServices = Sites.ToDictionary(s => s.Key, s => order.ServiceOrderItem!.First(i => i.Id == s.Key).Service!.Id!);
            state.Save();
            break;
        }

        case "execution":
            Execution.Print(await sp.GetRequiredService<Execution>().Drilldown(State.Require(state.Order, "order", "order"), ct));
            break;

        case "install-cpe" when args.Length > 1 && Sites.Any(s => s.Key == args[1]):
        {
            var setup = sp.GetRequiredService<Setup>();
            var site = Sites.First(s => s.Key == args[1]);
            await setup.InstallCpe(state, site.Key, site.CpeSerial, await setup.CpeDeviceSpec(ct), ct);
            break;
        }

        case "retry":
        {
            var orders = sp.GetRequiredService<Orders>();
            var orderId = State.Require(state.Order, "order", "order");
            await orders.Retry(orderId, ct);
            await orders.Follow(orderId, ct);
            break;
        }

        case "show":
            await sp.GetRequiredService<Inventory>().Print(state, ct);
            break;

        case "delete":
        {
            var inventory = sp.GetRequiredService<Inventory>();
            var orders = sp.GetRequiredService<Orders>();
            var vpn = await inventory.Service(State.Require(state.VpnService, "VPN", "order"), ct);
            var sites = new Dictionary<string, TmfApiClients.ServiceInventoryManagement.v4.Service>();
            foreach (var (key, id) in state.SiteServices) sites[key] = await inventory.Service(id, ct);
            await orders.Follow((await orders.Place(Orders.RemovalOrder(vpn, sites), ct)).Id!, ct);
            break;
        }

        case "walkthrough":
            return await sp.GetRequiredService<Walkthrough>().Run(ct);

        default:
            Console.WriteLine(Usage);
            return args.Length == 0 ? 0 : 2;
    }
}
catch (Exception error)
{
    return Errors.Report(error, builder.Configuration);
}
return 0;
