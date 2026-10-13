using AcmeVpn.Workers;
using ConductorSharp.Engine.Extensions;
using ConductorSharp.Engine.Util.Builders;
using ConductorSharp.Patterns.Extensions;
using ConductorSharp.Patterns.Services;
using Cxo.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using ServiceOrderingEngine.Extensions;

// Settings come from appsettings.json next to the binary, overridden by environment variables (Cxo__ClientSecret).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

// The workflows wait for resource orders, and the resource orders' notifications end the waits.
if (string.IsNullOrEmpty(builder.Configuration["Listener:CallbackUrl"]))
{
    Console.Error.WriteLine("""
        AcmeVpn.Workers: set Listener__CallbackUrl to the address CxO should post resource order notifications to —
        this machine, port 5652, path /tmf652, as seen from CxO. For example:
          Docker Desktop:            http://host.docker.internal:5652/tmf652
          Linux, CxO on this machine: http://<this machine's IP address>:5652/tmf652
        """);
    return 1;
}

builder.Services
    .AddConductorSharp(baseUrl: builder.Configuration["Conductor:BaseUrl"]!)
    .AddExecutionManager(maxConcurrentWorkers: 10, sleepInterval: 500, longPollInterval: 100,
        domain: null, typeof(Program).Assembly)
    // Workflows wait for resource orders with SignalWait. The in-memory store keeps the waits in this process only;
    // a production worker host uses a persistent store, so waits survive a restart.
    .AddSignalWait<InMemorySignalStore>()
    .AddPipelines(p => p.AddRequestResponseLogging());

builder.Services
    .AddHttpClient("ConductorSharp.Client.DefaultHttpClient")
    .AddResilienceHandler("workflow-engine", pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 8,
        BackoffType = DelayBackoffType.Exponential,
        Delay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromSeconds(30),
    }));

builder.AddCxoClients();
builder.Services.AddResourceOrderLinker();
builder.Services.AddSingleton<ResourceSpecifications>();
builder.Services.AddTransient<ResourceOrders>();
builder.Services.AddTransient<ResourceOrderSignals>();
builder.Services.AddSingleton<ResourceOrderInbox>();
builder.Services.AddHostedService<ResourceOrderSubscription>();
builder.Services.AddHostedService<ResourceOrderFollower>();

// Service workers: plan, order resources, link what the resource orders created.
builder.Services.RegisterWorkerTask<PlanVpn.Handler>();
builder.Services.RegisterWorkerTask<PlanSite.Handler>();
builder.Services.RegisterWorkerTask<OrderVpnResources.Handler>();
builder.Services.RegisterWorkerTask<OrderSiteResources.Handler>();
builder.Services.RegisterWorkerTask<OrderRemoval.Handler>();
builder.Services.RegisterWorkerTask<LinkResources.Handler>();

// Resource workers, run by the resource ordering engine's workflows.
builder.Services.RegisterWorkerTask<CheckCpe.Handler>();
builder.Services.RegisterWorkerTask<Configure.Handler>();
builder.Services.RegisterWorkerTask<Unconfigure.Handler>();

var owner = new BuildConfiguration { DefaultOwnerEmail = "integrations@acme.example.com" };
builder.Services.RegisterWorkflow<VpnPrepare>(owner);
builder.Services.RegisterWorkflow<VpnFulfill>(owner);
builder.Services.RegisterWorkflow<SitePrepare>(owner);
builder.Services.RegisterWorkflow<SiteFulfill>(owner);
builder.Services.RegisterWorkflow<VpnRelease>(owner);
builder.Services.RegisterWorkflow<CheckCpeWorkflow>(owner);
builder.Services.RegisterWorkflow<ConfigureWorkflow>(owner);
builder.Services.RegisterWorkflow<UnconfigureWorkflow>(owner);

// Say so at once if the workflow engine cannot be reached, rather than retrying for minutes.
var engine = builder.Configuration["Conductor:BaseUrl"]!;
try
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    (await probe.GetAsync(new Uri(new Uri(engine), "health"))).EnsureSuccessStatusCode();
}
catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
{
    Console.Error.WriteLine($"AcmeVpn.Workers: cannot reach the workflow engine at {engine} ({error.Message}). Check Conductor__BaseUrl.");
    return 1;
}

var app = builder.Build();

// Answer at once and process afterwards: a slow answer counts as a failed delivery and is retried.
app.MapPost("/tmf652", (System.Text.Json.Nodes.JsonObject notification, ResourceOrderInbox inbox) =>
{
    inbox.Accept(notification);
    return Results.Accepted();
});

try
{
    await app.RunAsync();
}
catch (Exception error) when (error is IOException or HttpRequestException or ApiClientCommons.Exceptions.ApiClientException or InvalidOperationException)
{
    // The usual causes: the port is taken, or CxO cannot be reached or refuses the client.
    Console.Error.WriteLine($"AcmeVpn.Workers: {error.Message}");
    return 1;
}
return 0;
