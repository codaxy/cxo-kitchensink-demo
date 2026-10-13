using ServiceActivationManagement.Client.v4;
using ServiceActivationManagement.Client.v4.Generated;
using Monitor = ServiceActivationManagement.Client.v4.Generated.Monitor;

namespace Acme.Cli;

/// <summary>
/// Step 7, without an order: Service Activation Management (TMF640) switches a service directly by running
/// its mapped activation workflow, and tracks the change as a monitor.
/// </summary>
public class Activation(IServiceActivationManagement4ApiClient activation)
{
    /// <summary>Requests the state change. Only one change per service runs at a time; a second one gets 409.</summary>
    public Task<Monitor> Request(string serviceId, ServiceStateType state, CancellationToken ct) =>
        activation.PatchServiceAsync(serviceId, new Service_Update { State = state }, ct);

    public async Task<Monitor> Follow(string monitorId, CancellationToken ct)
    {
        for (var i = 0; i < 120; i++)
        {
            var monitor = await activation.GetMonitorAsync(monitorId, ct);
            if (monitor.State is "Completed" or "Failed") return monitor;
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException($"Monitor {monitorId} did not finish. Is the worker application running?");
    }

    public async Task<Monitor[]> History(string serviceId, CancellationToken ct) =>
        (await activation.ListMonitorsAsync(new ListMonitorsQueryParams { ServiceIds = [serviceId] }, ct)).Items;
}
