using System.Text.Json.Nodes;
using Acme.Listener;
using Cxo.Clients;

// Step 5, "Notifications instead of polling": an order system's endpoint for TMF641 events.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

var callback = builder.Configuration["Listener:CallbackUrl"];
if (string.IsNullOrEmpty(callback))
{
    Console.Error.WriteLine("""
        Acme.Listener: set Listener__CallbackUrl to the address CxO should post notifications to — this machine,
        port 5641, path /tmf641, as seen from CxO. For example:
          Docker Desktop:            http://host.docker.internal:5641/tmf641
          Linux, CxO on this machine: http://<this machine's IP address>:5641/tmf641
        """);
    return 1;
}

builder.AddCxoClients();
builder.Services.AddSingleton<Inbox>();
builder.Services.AddHostedService<Subscription>();
builder.Services.AddHostedService<OrderFollower>();

var app = builder.Build();

// Answer at once and process afterwards: a slow answer counts as a failed delivery and is retried.
app.MapPost("/tmf641", (JsonObject notification, Inbox inbox) =>
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
    Console.Error.WriteLine($"Acme.Listener: {error.Message}");
    return 1;
}
return 0;
