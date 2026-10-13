using Acme.Workers;
using ConductorSharp.Engine.Extensions;
using ConductorSharp.Engine.Util.Builders;
using Cxo.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Polly;

// Settings come from appsettings.json next to the binary, overridden by environment variables (Cxo__ClientSecret).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

// Where the workflow engine is, and how often to ask it for work.
builder.Services
    .AddConductorSharp(baseUrl: builder.Configuration["Conductor:BaseUrl"]!)
    .AddExecutionManager(maxConcurrentWorkers: 10, sleepInterval: 500, longPollInterval: 100,
        domain: null, typeof(Program).Assembly)
    .AddPipelines(p => p.AddRequestResponseLogging());

// The worker host stops polling for good if a call to the engine fails, so retry transient errors.
builder.Services
    .AddHttpClient("ConductorSharp.Client.DefaultHttpClient")
    .AddResilienceHandler("workflow-engine", pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 8,
        BackoffType = DelayBackoffType.Exponential,
        Delay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromSeconds(30),
    }));

// The workers call CxO's TMF APIs through the gateway, with the typed clients from TmfApiClients.
builder.AddCxoClients();
builder.Services.AddSingleton<ResourceSpecifications>();

// Workers: one task definition each, polled by this process.
builder.Services.RegisterWorkerTask<PlanAccess.Handler>();
builder.Services.RegisterWorkerTask<CheckAccess.Handler>();
builder.Services.RegisterWorkerTask<ConfigureNetwork.Handler>();
builder.Services.RegisterWorkerTask<RecordResources.Handler>();
builder.Services.RegisterWorkerTask<RegisterCpe.Handler>();
builder.Services.RegisterWorkerTask<ReleaseResources.Handler>();
builder.Services.RegisterWorkerTask<SetAdminState.Handler>();

// Workflows: published to the workflow engine when the process starts.
var owner = new BuildConfiguration { DefaultOwnerEmail = "integrations@acme.example.com" };
builder.Services.RegisterWorkflow<InternetPrepare>(owner);
builder.Services.RegisterWorkflow<InternetCheck>(owner);
builder.Services.RegisterWorkflow<InternetFulfill>(owner);
builder.Services.RegisterWorkflow<CpeFulfill>(owner);
builder.Services.RegisterWorkflow<Release>(owner);
builder.Services.RegisterWorkflow<Activate>(owner);

// Say so at once if the workflow engine cannot be reached, rather than retrying for minutes.
var engine = builder.Configuration["Conductor:BaseUrl"]!;
try
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    (await probe.GetAsync(new Uri(new Uri(engine), "health"))).EnsureSuccessStatusCode();
}
catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
{
    Console.Error.WriteLine($"Acme.Workers: cannot reach the workflow engine at {engine} ({error.Message}). Check Conductor__BaseUrl.");
    return 1;
}

await builder.Build().RunAsync();
return 0;
