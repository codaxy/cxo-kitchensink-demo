using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TmfApiClients.ResourceCatalogManagement.v4;
using Inv = TmfApiClients.ResourceInventoryManagement.v4;

namespace Acme.Workers;

/// <summary>Finds resource specifications by name in the resource catalog, once per name.</summary>
public class ResourceSpecifications(IResourceCatalogManagement4ApiClient catalog)
{
    private readonly ConcurrentDictionary<string, string> _ids = new();

    public async Task<Inv.ResourceSpecificationRef> Ref(string name, CancellationToken cancellationToken)
    {
        if (!_ids.TryGetValue(name, out var id))
        {
            var found = await catalog.ListResourceSpecificationsAsync(new() { Name = name, Limit = 1 }, cancellationToken);
            id = found.Items.FirstOrDefault()?.Id
                ?? throw new InvalidOperationException($"No resource specification named {name}. Run the CLI's setup first.");
            _ids[name] = id;
        }
        return new Inv.ResourceSpecificationRef { Id = id, Name = name };
    }
}

static class CharacteristicExtensions
{
    public static Inv.Characteristic Pair(string name, JsonNode value) => new() { Name = name, Value = value };

    public static JsonNode? Find(this IEnumerable<Inv.Characteristic>? characteristics, string name) =>
        characteristics?.FirstOrDefault(c => c.Name == name)?.Value;
}
