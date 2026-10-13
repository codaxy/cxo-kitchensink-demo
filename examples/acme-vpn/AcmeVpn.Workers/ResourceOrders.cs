using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ApiClientCommons.Utilities;
using ConductorSharp.Patterns.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceOrderingEngine.Extensions;
using TmfApiClients.ResourceOrderingManagement.v4;
using TaskResultStatus = ConductorSharp.Client.Generated.TaskResultStatus;

namespace AcmeVpn.Workers;

/// <summary>
/// Creates the resource order for a service order item — once. The engine re-runs a failed phase when the service
/// order is retried, so the worker first looks for the resource order it created on an earlier attempt: a failed one
/// is retried (it resumes where it stopped), any other is reused. Only when there is none is a new one created and
/// linked to the service order item, so the order execution drilldown shows it under that item.
/// </summary>
public class ResourceOrders(IResourceOrderingManagement4ApiClient resourceOrdering, IResourceOrderLinker linker, ILogger<ResourceOrders> log)
{
    /// <summary>Marks the resource orders this worker host creates, so its listener receives only theirs.</summary>
    public const string ExternalIdPrefix = "acme-vpn:";

    /// <summary>The key a SignalWait waits on for a resource order.</summary>
    public static string SignalKey(string resourceOrderId) => $"ro:{resourceOrderId}";

    public async Task<ResourceOrder> Ensure(string serviceOrderId, string itemId, Func<ResourceOrderBuilder> build, CancellationToken ct)
    {
        var existing = (await resourceOrdering.ListResourceOrdersAsync(
            new() { ParentOrderId = [$"so:{serviceOrderId}:{itemId}"] }, ct)).Items.FirstOrDefault();

        if (existing is null)
        {
            var order = build().WithExternalId($"{ExternalIdPrefix}{serviceOrderId}:{itemId}").Build();
            var created = await linker.CreateAndLinkResourceOrderAsync(serviceOrderId, itemId, order, ct);
            log.LogInformation("Resource order {ResourceOrder} created for item {Item} of {Order}", created.Id, itemId, serviceOrderId);
            return created;
        }

        if (existing.State is ResourceOrderStateType.Failed or ResourceOrderStateType.Partial)
        {
            await resourceOrdering.MergePatchResourceOrderAsync(existing.Id!, new Partial<ResourceOrder_Update>
            {
                Data = new() { State = ResourceOrderStateType.Acknowledged },
                DefinedKeys = ["state"],
            }, ct);
            log.LogInformation("Resource order {ResourceOrder} retried for item {Item} of {Order}", existing.Id, itemId, serviceOrderId);
        }
        return existing;
    }
}

/// <summary>
/// Completes the SignalWait a workflow waits on once its resource order has finished. A failed resource order fails
/// the wait with the resource order item's structured error, so the reason reaches the service order item.
/// </summary>
public class ResourceOrderSignals(IResourceOrderingManagement4ApiClient resourceOrdering, ISignalService signals, ILogger<ResourceOrderSignals> log)
{
    public async Task SignalIfFinished(string resourceOrderId, CancellationToken ct)
    {
        // Decide on the order's current state, not on the event: a late or repeated notification of an earlier
        // attempt must not end the wait of a retry.
        var order = await resourceOrdering.GetResourceOrderAsync(resourceOrderId, ct);
        var output = new Dictionary<string, object> { ["resourceOrderId"] = order.Id! };
        TaskResultStatus status;

        if (order.State == ResourceOrderStateType.Completed)
            status = TaskResultStatus.COMPLETED;
        else if (order.State is ResourceOrderStateType.Failed or ResourceOrderStateType.Partial
                 or ResourceOrderStateType.Rejected or ResourceOrderStateType.Cancelled)
        {
            status = TaskResultStatus.FAILED;
            // Items that only depended on the one that failed carry the generic UNCLASSIFIED error; report the cause.
            var errors = (order.OrderItem ?? []).SelectMany(i => i.ErrorMessage ?? []).ToList();
            var error = errors.FirstOrDefault(e => e.Code != "UNCLASSIFIED") ?? errors.FirstOrDefault();
            output["structured_error"] = new Dictionary<string, object?>
            {
                ["code"] = error?.Code ?? "UNCLASSIFIED",
                ["reason"] = error?.Reason ?? $"The resource order ended {order.State}.",
                ["message"] = error?.Message,
                ["reference_error"] = $"/tmf-api/resourceOrderingManagement/v4/resourceOrder/{order.Id}",
                ["version"] = 1,
            };
        }
        else return;

        // A signal that arrives before the workflow starts waiting is kept, and ends the wait as soon as it begins.
        await signals.SendSignalAsync(ResourceOrders.SignalKey(order.Id!), status, output, ct);
        log.LogInformation("Resource order {ResourceOrder} finished {State}; signalled", order.Id, order.State);
    }
}

/// <summary>Takes TMF652 notifications from the endpoint, drops repeats, and queues the rest.</summary>
public class ResourceOrderInbox
{
    private readonly Channel<string> _finished = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, bool> _seen = new();

    public ChannelReader<string> Finished => _finished.Reader;

    public void Accept(JsonObject notification)
    {
        var eventId = (string?)notification["eventId"];
        if (eventId is not null && !_seen.TryAdd(eventId, true)) return;
        if ((string?)notification["event"]?["resourceOrder"]?["id"] is { } resourceOrderId)
            _finished.Writer.TryWrite(resourceOrderId);
    }
}

/// <summary>
/// Registers this worker host for TMF652 notifications when it starts, and removes the registration when it stops.
/// The query lets through only final state changes of the resource orders this host created.
/// </summary>
public class ResourceOrderSubscription(IResourceOrderingManagement4ApiClient resourceOrdering, IConfiguration configuration, ILogger<ResourceOrderSubscription> log)
    : IHostedService
{
    private string? _id;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var listener = await resourceOrdering.RegisterListenerAsync(new EventSubscriptionInput
        {
            Callback = configuration["Listener:CallbackUrl"]!,
            Query = "eventType=ResourceOrderStateChangeEvent"
                + "&event.resourceOrder.state=completed,failed,partial,rejected,cancelled"
                + $"&event.resourceOrder.externalId.regex=^{ResourceOrders.ExternalIdPrefix}",
        }, cancellationToken);
        _id = listener.Id;
        log.LogInformation("Listening for resource order notifications at {Callback} (listener {Id})", listener.Callback, listener.Id);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_id is not null)
            await resourceOrdering.UnregisterListenerAsync(_id, cancellationToken);
    }
}

/// <summary>
/// Signals the waits: for every notification of a finished resource order, and — because an order finishes even when
/// its notifications cannot be delivered — once a minute for any wait still open, by reading its resource order.
/// </summary>
public class ResourceOrderFollower(ResourceOrderInbox inbox, IServiceScopeFactory scopes, ILogger<ResourceOrderFollower> log) : BackgroundService
{
    private static readonly TimeSpan ReconcileEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(FromNotifications(stoppingToken), Reconcile(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task FromNotifications(CancellationToken ct)
    {
        await foreach (var resourceOrderId in inbox.Finished.ReadAllAsync(ct))
            await Safely(signals => signals.SignalIfFinished(resourceOrderId, ct), $"resource order {resourceOrderId}");
    }

    private async Task Reconcile(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Safely(async signals =>
            {
                using var scope = scopes.CreateScope();
                foreach (var waiter in await scope.ServiceProvider.GetRequiredService<ISignalStore>().GetPendingWaitersAsync(ct))
                    if (waiter.SignalKey.StartsWith("ro:"))
                        await signals.SignalIfFinished(waiter.SignalKey["ro:".Length..], ct);
            }, "the open waits");
            await Task.Delay(ReconcileEvery, ct);
        }
    }

    private async Task Safely(Func<ResourceOrderSignals, Task> work, string what)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await work(scope.ServiceProvider.GetRequiredService<ResourceOrderSignals>());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Could not check {What}; it is checked again at the next reconciliation", what);
        }
    }
}
