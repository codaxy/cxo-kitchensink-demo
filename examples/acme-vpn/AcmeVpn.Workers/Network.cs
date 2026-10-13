using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TmfApiClients.ResourceCatalogManagement.v4;
using Inv = TmfApiClients.ResourceInventoryManagement.v4;

namespace AcmeVpn.Workers;

/// <summary>
/// What the workers know about the network: which provider edge port serves each Acme office. In a real deployment
/// this comes from the network inventory or a planning system; the example keeps it in a table.
/// </summary>
public static class Network
{
    private static readonly Dictionary<string, (string PeRouter, string PeInterface, int Index)> Offices = new()
    {
        ["Acme HQ"] = ("pe1.example.net", "ge-0/0/1", 1),
        ["Acme Branch North"] = ("pe2.example.net", "ge-0/0/4", 2),
        ["Acme Branch South"] = ("pe3.example.net", "ge-0/0/2", 3),
    };

    /// <summary>The access ports carry 1 Gbps.</summary>
    public const int AccessPortMbps = 1000;

    public static (string PeRouter, string PeInterface, int Index) Office(string siteName) =>
        Offices.TryGetValue(siteName, out var office)
            ? office
            : throw new InvalidOperationException($"The network plan has no provider edge port for site {siteName}.");
}

/// <summary>Finds resource specifications by name in the resource catalog, once per name.</summary>
public class ResourceSpecifications(IResourceCatalogManagement4ApiClient catalog)
{
    private readonly ConcurrentDictionary<string, string> _ids = new();

    public async Task<string> Id(string name, CancellationToken cancellationToken)
    {
        if (!_ids.TryGetValue(name, out var id))
        {
            var found = await catalog.ListResourceSpecificationsAsync(new() { Name = name, Limit = 1 }, cancellationToken);
            id = found.Items.FirstOrDefault()?.Id
                ?? throw new InvalidOperationException($"No resource specification named {name}. Run the CLI's setup first.");
            _ids[name] = id;
        }
        return id;
    }
}

static class CharacteristicExtensions
{
    public static JsonNode? Find(this IEnumerable<Inv.Characteristic>? characteristics, string name) =>
        characteristics?.FirstOrDefault(c => c.Name == name)?.Value;
}
