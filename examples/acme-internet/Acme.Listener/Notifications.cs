using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ApiClientCommons;
using TmfApiClients.ServiceOrderingManagement.v4;

namespace Acme.Listener;

/// <summary>Takes notifications from the endpoint, drops repeats, and queues the rest for processing.</summary>
public class Inbox
{
    private readonly Channel<JsonObject> _queue = Channel.CreateUnbounded<JsonObject>();
    private readonly ConcurrentDictionary<string, bool> _seen = new();

    public ChannelReader<JsonObject> Reader => _queue.Reader;

    /// <summary>A notification can arrive more than once; its eventId tells repeats apart.</summary>
    public void Accept(JsonObject notification)
    {
        var eventId = (string?)notification["eventId"];
        if (eventId is null || _seen.TryAdd(eventId, true))
            _queue.Writer.TryWrite(notification);
    }
}

/// <summary>
/// Registers the listener on the TMF641 hub when the application starts, and removes it when it stops.
/// A production order system keeps its registration instead, so events raised while it is down wait for it;
/// the example removes it so that it leaves nothing behind.
/// </summary>
public class Subscription(IServiceOrderingManagement4ApiClient ordering, IConfiguration configuration, ILogger<Subscription> log) : IHostedService
{
    private string? _id;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // No query: every TMF641 event. Registering the same callback again returns the existing listener.
        var listener = await ordering.RegisterListenerAsync(
            new EventSubscriptionInput { Callback = configuration["Listener:CallbackUrl"]! }, cancellationToken);
        _id = listener.Id;
        log.LogInformation("Listening for TMF641 events at {Callback} (listener {Id})", listener.Callback, listener.Id);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_id is not null)
            await ordering.UnregisterListenerAsync(_id, cancellationToken);
    }
}

/// <summary>
/// Follows orders from their notifications. A retried notification can arrive after a later one, so it decides on
/// the order's state, and reads the order itself once it is final.
/// </summary>
public class OrderFollower(Inbox inbox, IServiceOrderingManagement4ApiClient ordering) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = ApiClientBase.CreateDefaultJsonSerializerOptions();
    private static readonly string[] FinalStates = ["completed", "failed", "partial", "rejected", "cancelled"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Follow(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task Follow(CancellationToken stoppingToken)
    {
        await foreach (var notification in inbox.Reader.ReadAllAsync(stoppingToken))
        {
            var type = (string?)notification["eventType"];
            var payload = notification["event"]?["serviceOrder"];
            if (payload is null) continue;
            var order = payload.Deserialize<ServiceOrder>(Json)!;

            Console.WriteLine($"{(DateTimeOffset?)notification["eventTime"]:HH:mm:ss} {type,-40} {Describe(order)}");

            if (type == "ServiceOrderStateChangeEvent" && FinalStates.Contains((string?)payload["state"]))
                await Report(order.Id!, stoppingToken);
        }
    }

    // The notification is the state at the time of the event; read the order for the current one.
    private async Task Report(string orderId, CancellationToken ct)
    {
        var order = await ordering.GetServiceOrderAsync(orderId, ct);
        Console.WriteLine($"         order {orderId} is final — {Describe(order)}");
        foreach (var item in order.ServiceOrderItem ?? [])
        foreach (var error in item.ErrorMessage ?? [])
            Console.WriteLine($"         item {item.Id}: {error.Code} — {error.Reason}");
    }

    private static string Describe(ServiceOrder order) =>
        $"order {order.Id?[..8]} {Name(order.State)}: "
        + string.Join(", ", (order.ServiceOrderItem ?? []).Select(i => $"item {i.Id} {Name(i.State)}"))
        + (order.Milestone?.Count(m => m.Status == "reached") is > 0 and var reached ? $" ({reached} milestone(s) reached)" : "");

    private static string Name<T>(T? state) where T : struct, Enum =>
        state is null ? "?" : JsonNamingPolicy.CamelCase.ConvertName(state.Value.ToString());
}
