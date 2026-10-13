using Acme.Cli;
using Cxo.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SamState = ServiceActivationManagement.Client.v4.Generated.ServiceStateType;

const string Usage = """
    Works through the CxO User Guide's Acme example, step by step.

      acme keycloak-client  Before you start: create the Keycloak client and print its secret (needs Keycloak admin)
      acme setup            Steps 1-3: customer and site, catalog, workflow maps (safe to re-run)
      acme order            Steps 4-5: place the guide's order and follow it to the end
      acme show             Step 6:    the services and resources the order created
      acme modify <mbps>    Step 7:    change the Internet speed (100, 1000 or 10000)
      acme deactivate       Step 7:    switch the Internet service off without an order (TMF640)
      acme activate         Step 7:    switch it back on
      acme delete           Step 7:    remove the Internet service, then the CPE
      acme walkthrough      All of the above plus the failure cases, with a check for each outcome

    Start the worker application (Acme.Workers) first; orders wait for it.
    """;

// Settings come from appsettings.json next to the binary, overridden by environment variables (Cxo__ClientSecret).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
// Only command output goes to stdout, so `export Cxo__ClientSecret=$(acme keycloak-client)` captures just the secret.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.AddCxoClients();
builder.Services.AddTransient<KeycloakClient>().AddTransient<Setup>().AddTransient<Orders>().AddTransient<Inventory>()
    .AddTransient<Activation>().AddTransient<Walkthrough>();
using var host = builder.Build();
var sp = host.Services;
var ct = CancellationToken.None;

var state = State.Load();
// Configuration problems end in one line saying what to check, not a stack trace.
try
{
    switch (args.FirstOrDefault())
    {
        case "keycloak-client":
            Console.WriteLine(await sp.GetRequiredService<KeycloakClient>()
                .Create(args.Length > 1 ? args[1] : builder.Configuration["Cxo:ClientId"]!, ct));
            break;

        case "setup":
            await sp.GetRequiredService<Setup>().Run(state, ct);
            break;

        case "order":
        {
            var orders = sp.GetRequiredService<Orders>();
            var order = await orders.Follow((await orders.Place(Orders.GuideOrder(state), ct)).Id!, ct);
            state.Order = order.Id;
            state.CpeService = order.ServiceOrderItem?[0].Service?.Id;
            state.InternetService = order.ServiceOrderItem?[1].Service?.Id;
            state.Save();
            break;
        }

        case "show":
        {
            var inventory = sp.GetRequiredService<Inventory>();
            await inventory.Print(State.Require(state.InternetService, "Internet service", "order"), ct);
            await inventory.Print(State.Require(state.CpeService, "CPE service", "order"), ct);
            break;
        }

        case "modify" when args.Length > 1 && int.TryParse(args[1], out var mbps):
        {
            var current = await sp.GetRequiredService<Inventory>().Service(State.Require(state.InternetService, "Internet service", "order"), ct);
            var orders = sp.GetRequiredService<Orders>();
            await orders.Follow((await orders.Place(Orders.Modify(state, current, mbps), ct)).Id!, ct);
            break;
        }

        case "deactivate" or "activate":
        {
            var activation = sp.GetRequiredService<Activation>();
            var serviceId = State.Require(state.InternetService, "Internet service", "order");
            var monitor = await activation.Request(serviceId, args[0] == "activate" ? SamState.Active : SamState.Inactive, ct);
            Console.WriteLine($"  Monitor {monitor.Id} {monitor.State}");
            monitor = await activation.Follow(monitor.Id!, ct);
            Console.WriteLine($"  Monitor {monitor.Id} {monitor.State}");
            break;
        }

        case "delete":
        {
            // The engine does not check what depends on a service: remove the Internet service before its CPE.
            var inventory = sp.GetRequiredService<Inventory>();
            var orders = sp.GetRequiredService<Orders>();
            foreach (var id in new[] { state.InternetService, state.CpeService })
            {
                var service = await inventory.Service(State.Require(id, "service", "order"), ct);
                await orders.Follow((await orders.Place(Orders.Delete(service), ct)).Id!, ct);
            }
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
