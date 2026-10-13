using ResourceOrderingEngine.Client;
using ServiceOrderingEngine;
using ServiceOrderingEngine.Client;
using TmfApiClients.CustomerManagement.v4;
using TmfApiClients.GeographicAddressManagement.v4;
using TmfApiClients.GeographicSiteManagement.v4;
using TmfApiClients.PartyManagement.v4;
using TmfApiClients.ResourceCatalogManagement.v4;
using TmfApiClients.ResourceInventoryManagement.v4;
using TmfApiClients.ServiceCatalogManagement.v4;
using static AcmeVpn.Names;
using Cust = TmfApiClients.CustomerManagement.v4;
using Rom = ResourceOrderingEngine;
using Site = TmfApiClients.GeographicSiteManagement.v4;

namespace AcmeVpn.Cli;

/// <summary>
/// Registers the customer and its three offices, installs the CPEs at HQ and Branch North (Branch South's comes
/// later), models the VPN and its sites in the catalog, and maps the workflows of both ordering engines.
/// Everything is looked up by name first, so setup can be run again safely.
/// </summary>
public class Setup(
    IPartyManagement4ApiClient party,
    ICustomerManagement4ApiClient customers,
    IGeographicAddressManagement4ApiClient addresses,
    IGeographicSiteManagement4ApiClient sites,
    IResourceCatalogManagement4ApiClient resourceCatalog,
    IResourceInventoryManagement4ApiClient resources,
    IServiceCatalogManagement4ApiClient serviceCatalog,
    IServiceOrderingEngineClient serviceEngine,
    IResourceOrderingEngineClient resourceEngine)
{
    public async Task Run(State state, CancellationToken ct)
    {
        await RefuseToChangeOtherSpecifications(ct);
        await RegisterCustomerAndSites(state, ct);
        var specs = await ModelTheService(state, ct);
        foreach (var (key, _, _, serial) in Sites.Where(s => s.Key != "south"))
            await InstallCpe(state, key, serial, specs.CpeDevice, ct);
        await MapServiceWorkflows(state, ct);
        await MapResourceWorkflows(specs, ct);
        state.Save();
    }

    /// <summary>Registers the CPE at a site: a physical resource the site attachment's feasibility check looks for.</summary>
    public async Task InstallCpe(State state, string siteKey, string serial, string cpeDeviceSpec, CancellationToken ct)
    {
        var name = $"CPE {serial}";
        if ((await resources.ListResourcesAsync(new() { Name = name }, ct)).Items.Length == 0)
            await resources.CreateResourceAsync(new PhysicalResource
            {
                Name = name,
                SerialNumber = serial,
                ResourceSpecification = new() { Id = cpeDeviceSpec, Name = ResourceSpecs.CpeDevice },
                Place = new() { Id = state.Sites[siteKey], Role = "installationAddress", ReferredType = "GeographicSite" },
            }, ct);
        Step("CPE installed", $"{name} at {Sites.First(s => s.Key == siteKey).Name}");
    }

    /// <summary>Removes a site's CPE from inventory, so the walkthrough can start with Branch South's missing.</summary>
    public async Task UninstallCpe(string serial, CancellationToken ct)
    {
        foreach (var cpe in (await resources.ListResourcesAsync(new() { Name = $"CPE {serial}" }, ct)).Items)
            await resources.DeleteResourceAsync(cpe.Id!, ct);
    }

    public async Task<string> CpeDeviceSpec(CancellationToken ct) =>
        (await resourceCatalog.ListResourceSpecificationsAsync(new() { Name = ResourceSpecs.CpeDevice }, ct)).Items.FirstOrDefault()?.Id
        ?? throw new UserError("Run 'vpn setup' first.");

    // Setup reuses what it finds by name: make sure what it finds is the example's own before changing anything.
    private async Task RefuseToChangeOtherSpecifications(CancellationToken ct)
    {
        var expected = new Dictionary<string, string[]>
        {
            [ServiceSpecs.Vpn] = [Characteristics.VpnName],
            [ServiceSpecs.Site] = [Characteristics.Bandwidth, Characteristics.CpeSerialNumber],
        };
        foreach (var (name, characteristics) in expected)
        {
            var spec = (await serviceCatalog.ListServiceSpecificationsAsync(new() { Name = name }, ct)).Items.FirstOrDefault();
            if (spec is null) continue;
            var found = (spec.SpecCharacteristic ?? []).Select(c => c.Name).Order();
            var maps = (await serviceEngine.ListServiceWorkflowMapsAsync(new ListServiceWorkflowsQuery { ServiceSpecificationId = spec.Id }, ct)).Mappings;
            var foreign = maps.SelectMany(m => new[]
                {
                    m.StageMap.PreparationWorkflow, m.StageMap.FeasibilityCheckWorkflow, m.StageMap.FulfillmentWorkflow,
                    m.StageMap.PreparationRollbackWorkflow, m.StageMap.FeasibilityCheckRollbackWorkflow,
                    m.StageMap.FulfillmentRollbackWorkflow, m.StageMap.CancellationWorkflow,
                })
                .Select(w => w?.Name ?? "").Where(w => w != "" && !w.StartsWith("acme_vpn_") && !w.StartsWith("som_gen_")).Distinct().ToList();
            if (!found.SequenceEqual(characteristics.Order()) || foreign.Count > 0)
                throw new SetupConflictException(
                    $"This environment already has a service specification named {name} that is not the example's. "
                    + "Setup changes nothing; run the example in another environment.");
        }
    }

    private async Task RegisterCustomerAndSites(State state, CancellationToken ct)
    {
        var organization = (await party.ListOrganizationsAsync(new() { Name = "Acme Corp" }, ct)).Items.FirstOrDefault()?.Id
            ?? (await party.CreateOrganizationAsync(new Organization_Create { TradingName = "Acme Corp", Name = "Acme Corp", IsLegalEntity = true }, ct)).Id!;
        state.Customer = (await customers.ListCustomersAsync(new() { Name = "Acme Corp" }, ct)).Items.FirstOrDefault()?.Id
            ?? (await customers.CreateCustomerAsync(new Customer_Create
            {
                Name = "Acme Corp",
                EngagedParty = new Cust.RelatedParty { Id = organization, Href = $"/tmf-api/party/v4/organization/{organization}", ReferredType = "Organization" },
            }, ct)).Id;
        Step("Customer", state.Customer);

        foreach (var (key, name, city, _) in Sites)
        {
            var address = (await addresses.ListGeographicAddressesAsync(new() { Name = [$"{name} address"] }, ct)).Items.FirstOrDefault()?.Id
                ?? (await addresses.CreateGeographicAddressAsync(new GeographicAddress_Create
                {
                    Name = $"{name} address", StreetName = "Main Street", StreetNr = "1", Postcode = "10000", City = city, Country = "Exampleland",
                }, ct)).Id!;
            state.Sites[key] = (await sites.ListGeographicSitesAsync(new() { Name = [name] }, ct)).Items.FirstOrDefault()?.Id
                ?? (await sites.CreateGeographicSiteAsync(new GeographicSite
                {
                    Name = name,
                    Status = "active",
                    Place = [new() { Id = address, Type = "PlaceRef", ReferredType = "GeographicAddress" }],
                    RelatedParty = [new Site.RelatedParty { Id = state.Customer!, Name = "Acme Corp", Role = "customer", ReferredType = "Customer" }],
                }, ct)).Id!;
            Step("Site", $"{name} {state.Sites[key]}");
        }
    }

    private record ResourceSpecIds(string Instance, string Attachment, string CpeInterface, string CpeDevice);

    private async Task<ResourceSpecIds> ModelTheService(State state, CancellationToken ct)
    {
        static ResourceSpecificationCharacteristic C(string name, string type) => new() { Name = name, ValueType = type };

        var instance = await ResourceSpec(new LogicalResourceSpecification
        {
            Name = ResourceSpecs.Instance, Version = "1.0",
            ResourceSpecCharacteristic = [C("vpnId", "integer"), C("routeTarget", "string")],
        }, ct);
        var attachment = await ResourceSpec(new LogicalResourceSpecification
        {
            Name = ResourceSpecs.Attachment, Version = "1.0",
            ResourceSpecCharacteristic =
            [
                C("peRouter", "string"), C("peInterface", "string"), C("peAddress", "string"),
                C("routeTarget", "string"), C("bandwidth", "integer"), C("cpeSerialNumber", "string"),
            ],
            ResourceSpecRelationship = [new() { Id = instance, Name = ResourceSpecs.Instance, RelationshipType = "isPartOf" }],
        }, ct);
        var cpeInterface = await ResourceSpec(new LogicalResourceSpecification
        {
            Name = ResourceSpecs.CpeInterface, Version = "1.0",
            ResourceSpecCharacteristic = [C("interfaceName", "string"), C("vlanId", "integer"), C("address", "string")],
            ResourceSpecRelationship = [new() { Id = instance, Name = ResourceSpecs.Instance, RelationshipType = "isPartOf" }],
        }, ct);
        var cpeDevice = await ResourceSpec(new PhysicalResourceSpecification { Name = ResourceSpecs.CpeDevice, Version = "1.0", Vendor = "Cisco" }, ct);

        // The customer chooses only the VPN's name, and each site's bandwidth and the CPE to use there.
        state.VpnSpec = await ServiceSpec(new ServiceSpecification_Create
        {
            Name = ServiceSpecs.Vpn, Version = "1.0", LifecycleStatus = "Launched",
            SpecCharacteristic =
            [
                new() { Name = Characteristics.VpnName, ValueType = "string", CharacteristicValueSpecification = [new() { ValueType = "string" }] },
            ],
            ResourceSpecification = [new() { Id = instance, Name = ResourceSpecs.Instance, ReferredType = "LogicalResourceSpecification" }],
        }, ct);
        state.SiteSpec = await ServiceSpec(new ServiceSpecification_Create
        {
            Name = ServiceSpecs.Site, Version = "1.0", LifecycleStatus = "Launched",
            SpecCharacteristic =
            [
                new()
                {
                    Name = Characteristics.Bandwidth, ValueType = "integer",
                    CharacteristicValueSpecification =
                    [
                        new() { ValueType = "integer", Value = 100, UnitOfMeasure = "Mbps", IsDefault = true },
                        new() { ValueType = "integer", Value = 1000, UnitOfMeasure = "Mbps" },
                    ],
                },
                new() { Name = Characteristics.CpeSerialNumber, ValueType = "string", CharacteristicValueSpecification = [new() { ValueType = "string" }] },
            ],
            ResourceSpecification =
            [
                new() { Id = attachment, Name = ResourceSpecs.Attachment, ReferredType = "LogicalResourceSpecification" },
                new() { Id = cpeInterface, Name = ResourceSpecs.CpeInterface, ReferredType = "LogicalResourceSpecification" },
            ],
        }, ct);

        var category = (await serviceCatalog.ListServiceCategoriesAsync(new() { Name = "VPN" }, ct)).Items.FirstOrDefault()?.Id
            ?? (await serviceCatalog.CreateServiceCategoryAsync(new ServiceCategory_Create { Name = "VPN", IsRoot = true }, ct)).Id!;
        if ((await serviceCatalog.ListServiceCatalogsAsync(new() { Name = "B2B VPN Services" }, ct)).Items.Length == 0)
            await serviceCatalog.CreateServiceCatalogAsync(new ServiceCatalog_Create { Name = "B2B VPN Services", Category = [new() { Id = category }] }, ct);
        foreach (var (name, spec) in new[] { ("Acme L3VPN", state.VpnSpec), ("Acme L3VPN site", state.SiteSpec) })
            if ((await serviceCatalog.ListServiceCandidatesAsync(new() { Name = name }, ct)).Items.Length == 0)
                await serviceCatalog.CreateServiceCandidateAsync(new ServiceCandidate_Create
                {
                    Name = name, ServiceSpecification = new() { Id = spec }, Category = [new() { Id = category }],
                }, ct);
        Step("Catalog", "B2B VPN Services");
        return new ResourceSpecIds(instance, attachment, cpeInterface, cpeDevice);
    }

    private async Task<string> ResourceSpec(ResourceSpecification spec, CancellationToken ct)
    {
        var found = (await resourceCatalog.ListResourceSpecificationsAsync(new() { Name = spec.Name }, ct)).Items.FirstOrDefault()
            ?? await resourceCatalog.CreateResourceSpecificationAsync(spec, ct);
        Step("Resource specification", $"{found.Name} {found.Id}");
        return found.Id!;
    }

    private async Task<string> ServiceSpec(ServiceSpecification_Create spec, CancellationToken ct)
    {
        var id = (await serviceCatalog.ListServiceSpecificationsAsync(new() { Name = spec.Name }, ct)).Items.FirstOrDefault()?.Id
            ?? (await serviceCatalog.CreateServiceSpecificationAsync(spec, ct)).Id!;
        Step("Service specification", $"{spec.Name} {id}");
        return id;
    }

    // The service ordering engine runs the VPN's and the sites' workflows. Unused slots get the generic no-ops,
    // and a failed fulfillment rolls back nothing: resource orders have no rollback, and a retry reuses their resources.
    private async Task MapServiceWorkflows(State state, CancellationToken ct)
    {
        await MapService(state.VpnSpec!, ServiceAction.Add, Workflows.VpnPrepare, Workflows.VpnFulfill, ct);
        await MapService(state.VpnSpec!, ServiceAction.Delete, Workflows.ServicePrepare, Workflows.VpnRelease, ct);
        await MapService(state.SiteSpec!, ServiceAction.Add, Workflows.SitePrepare, Workflows.SiteFulfill, ct);
        await MapService(state.SiteSpec!, ServiceAction.Delete, Workflows.ServicePrepare, Workflows.VpnRelease, ct);
    }

    private async Task MapService(string specId, ServiceAction action, string preparation, string fulfillment, CancellationToken ct)
    {
        static ServiceWorkflow W(string name) => new() { Name = name, Version = 1 };
        var stages = new StageMap
        {
            PreparationWorkflow = W(preparation),
            FeasibilityCheckWorkflow = W(Workflows.ServiceCheck),
            FulfillmentWorkflow = W(fulfillment),
            PreparationRollbackWorkflow = W(Workflows.ServicePrepareRollback),
            FeasibilityCheckRollbackWorkflow = W(Workflows.ServiceCheckRollback),
            FulfillmentRollbackWorkflow = W(Workflows.ServiceFulfillRollback),
            CancellationWorkflow = W(Workflows.ServiceCancel),
        };
        // Find this action's map among the specification's maps.
        var existing = (await serviceEngine.ListServiceWorkflowMapsAsync(new ListServiceWorkflowsQuery { ServiceSpecificationId = specId }, ct))
            .Mappings.FirstOrDefault(m => m.Action == action);
        if (existing is null)
            await serviceEngine.CreateServiceWorkflowMapAsync(new CreateServiceWorkflowMapping { ServiceSpecificationId = specId, Action = action, StageMap = stages }, ct);
        else
            await serviceEngine.UpdateServiceWorkflowMapAsync(new UpdateServiceWorkflowMapping { Id = existing.Id, ServiceSpecificationId = specId, Action = action, StageMap = stages }, ct);
        Step("Service workflow map", $"{action}: {preparation} / {fulfillment}");
    }

    // The resource ordering engine runs the resource workflows: configure on add, unconfigure on delete; a site
    // attachment is first checked for its CPE.
    private async Task MapResourceWorkflows(ResourceSpecIds specs, CancellationToken ct)
    {
        foreach (var spec in new[] { specs.Instance, specs.Attachment, specs.CpeInterface })
        {
            var check = spec == specs.Attachment ? Workflows.CheckCpe : Workflows.ResourceCheck;
            await MapResource(spec, Rom.ResourceAction.Add, check, Workflows.Configure, ct);
            await MapResource(spec, Rom.ResourceAction.Delete, Workflows.ResourceCheck, Workflows.Unconfigure, ct);
        }
    }

    private async Task MapResource(string specId, Rom.ResourceAction action, string check, string fulfillment, CancellationToken ct)
    {
        static Rom.ResourceWorkflow W(string name) => new() { Name = name, Version = 1 };
        var stages = new Rom.StageMap
        {
            PreparationWorkflow = W(Workflows.ResourcePrepare),
            FeasibilityCheckWorkflow = W(check),
            FulfillmentWorkflow = W(fulfillment),
            CancellationWorkflow = W(Workflows.ResourceCancel),
        };
        var existing = (await resourceEngine.ListResourceWorkflowMapsAsync(new Rom.ListResourceWorkflowsQuery { ResourceSpecificationId = specId }, ct))
            .Mappings.FirstOrDefault(m => m.Action == action);
        if (existing is null)
            await resourceEngine.CreateResourceWorkflowMapAsync(new Rom.CreateResourceWorkflowMapping { ResourceSpecificationId = specId, Action = action, StageMap = stages }, ct);
        else
            await resourceEngine.UpdateResourceWorkflowMapAsync(new Rom.UpdateResourceWorkflowMapping { Id = existing.Id, ResourceSpecificationId = specId, Action = action, StageMap = stages }, ct);
        Step("Resource workflow map", $"{action}: {check} / {fulfillment}");
    }

    private static void Step(string what, string? value) => Console.WriteLine($"  {what,-24} {value}");
}
