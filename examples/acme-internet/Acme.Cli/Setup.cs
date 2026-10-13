using System.Text.Json.Nodes;
using ServiceActivationManagement.Api.Grpc;
using ServiceActivationManagement.Client.Grpc;
using ServiceOrderingEngine;
using ServiceOrderingEngine.Client;
using TmfApiClients.CustomerManagement.v4;
using TmfApiClients.GeographicAddressManagement.v4;
using TmfApiClients.GeographicSiteManagement.v4;
using TmfApiClients.PartyManagement.v4;
using TmfApiClients.ResourceCatalogManagement.v4;
using TmfApiClients.ServiceCatalogManagement.v4;
using static Acme.Names;
using Cust = TmfApiClients.CustomerManagement.v4;
using Site = TmfApiClients.GeographicSiteManagement.v4;

namespace Acme.Cli;

/// <summary>
/// Steps 1 to 3 of the guide: register the customer and the site, model the service, and map the workflows.
/// Everything is looked up by name first, so setup can be run again safely.
/// </summary>
public class Setup(
    IPartyManagement4ApiClient party,
    ICustomerManagement4ApiClient customers,
    IGeographicAddressManagement4ApiClient addresses,
    IGeographicSiteManagement4ApiClient sites,
    IResourceCatalogManagement4ApiClient resourceCatalog,
    IServiceCatalogManagement4ApiClient serviceCatalog,
    IServiceOrderingEngineClient orderingEngine,
    IServiceActivationGrpcClient activationEngine)
{
    public async Task Run(State state, CancellationToken ct)
    {
        await RefuseToChangeOtherSpecifications(ct);
        await RegisterCustomerAndSite(state, ct);
        await ModelTheService(state, ct);
        await MapTheWorkflows(state, ct);
        state.Save();
    }

    /// <summary>
    /// Setup reuses what it finds by name. Before it changes anything, make sure that a service specification with
    /// one of the example's names is the example's own, and that its workflow maps point only at the example's
    /// workflows or CxO's generic ones — so running the example can never re-map a specification something else uses.
    /// </summary>
    private async Task RefuseToChangeOtherSpecifications(CancellationToken ct)
    {
        var expected = new Dictionary<string, string[]>
        {
            [ServiceSpecs.Internet] = [Characteristics.BandwidthDown, Characteristics.BandwidthUp],
            [ServiceSpecs.Device] = [Characteristics.SerialNumber, Characteristics.Model],
        };
        foreach (var (name, characteristics) in expected)
        {
            var spec = (await serviceCatalog.ListServiceSpecificationsAsync(new() { Name = name }, ct)).Items.FirstOrDefault();
            if (spec is null) continue;

            var found = (spec.SpecCharacteristic ?? []).Select(c => c.Name).Order();
            if (!found.SequenceEqual(characteristics.Order()))
                throw new SetupConflictException(
                    $"This environment already has a service specification named {name} that is not the example's "
                    + $"(its characteristics are {string.Join(", ", found)}). Setup changes nothing; run the example in another environment.");

            var maps = (await orderingEngine.ListServiceWorkflowMapsAsync(new ListServiceWorkflowsQuery { ServiceSpecificationId = spec.Id }, ct)).Mappings;
            var foreign = maps.SelectMany(m => new[]
                {
                    m.StageMap.PreparationWorkflow, m.StageMap.FeasibilityCheckWorkflow, m.StageMap.FulfillmentWorkflow,
                    m.StageMap.PreparationRollbackWorkflow, m.StageMap.FeasibilityCheckRollbackWorkflow,
                    m.StageMap.FulfillmentRollbackWorkflow, m.StageMap.CancellationWorkflow,
                })
                .Select(w => w?.Name ?? "")
                .Where(w => w != "" && !IsExampleOrGeneric(w))
                .Distinct().ToList();
            var activation = (await activationEngine.ListWorkflowMappingsAsync(new ListWorkflowMappingsQuery { ServiceSpecificationId = spec.Id }, ct)).Mappings;
            foreign.AddRange(activation.Select(m => m.WorkflowName).Where(w => w != Workflows.Activate));
            if (foreign.Count > 0)
                throw new SetupConflictException(
                    $"The service specification {name} is mapped to workflows that are not the example's ({string.Join(", ", foreign)}). "
                    + "Setup changes nothing; run the example in another environment.");
        }
    }

    private static bool IsExampleOrGeneric(string workflow) => workflow.StartsWith("acme_") || workflow.StartsWith("som_gen_");

    // Step 1: the organization, its customer record, the address, and the site at that address.
    // The services do not check that a referenced record exists, so create them in this order.
    private async Task RegisterCustomerAndSite(State state, CancellationToken ct)
    {
        state.Organization = (await party.ListOrganizationsAsync(new() { Name = "Acme Corp" }, ct)).Items.FirstOrDefault()?.Id
            ?? (await party.CreateOrganizationAsync(new Organization_Create
            {
                TradingName = "Acme Corp",
                Name = "Acme Corp",
                IsLegalEntity = true,
            }, ct)).Id;
        Step("Organization", state.Organization);

        state.Customer = (await customers.ListCustomersAsync(new() { Name = "Acme Corp" }, ct)).Items.FirstOrDefault()?.Id
            ?? (await customers.CreateCustomerAsync(new Customer_Create
            {
                Name = "Acme Corp",
                EngagedParty = new Cust.RelatedParty
                {
                    Id = state.Organization,
                    Href = $"/tmf-api/party/v4/organization/{state.Organization}",
                    ReferredType = "Organization",
                },
            }, ct)).Id;
        Step("Customer", state.Customer);

        state.Address = (await addresses.ListGeographicAddressesAsync(new() { Name = ["Acme HQ address"] }, ct)).Items.FirstOrDefault()?.Id
            ?? (await addresses.CreateGeographicAddressAsync(new GeographicAddress_Create
            {
                Name = "Acme HQ address",
                StreetName = "Main Street",
                StreetNr = "1",
                Postcode = "10000",
                City = "Exampletown",
                Country = "Exampleland",
            }, ct)).Id;
        Step("Address", state.Address);

        // The site refers to the address through place, and to the customer through relatedParty.
        state.Site = (await sites.ListGeographicSitesAsync(new() { Name = ["Acme HQ"] }, ct)).Items.FirstOrDefault()?.Id
            ?? (await sites.CreateGeographicSiteAsync(new GeographicSite
            {
                Name = "Acme HQ",
                Status = "active",
                Place = [new() { Id = state.Address, Type = "PlaceRef", ReferredType = "GeographicAddress" }],
                RelatedParty = [new Site.RelatedParty { Id = state.Customer!, Name = "Acme Corp", Role = "customer", ReferredType = "Customer" }],
            }, ct)).Id;
        Step("Site", state.Site);
    }

    // Step 2: the resource specifications the workers record, the two service specifications customers order,
    // and the catalog, categories and candidates that publish them.
    private async Task ModelTheService(State state, CancellationToken ct)
    {
        // Create the specifications a relationship points at first.
        var root = await ResourceSpec(new LogicalResourceSpecification { Name = ResourceSpecs.Root, Version = "1.0" }, ct);
        await ResourceSpec(new LogicalResourceSpecification
        {
            Name = ResourceSpecs.Interface,
            Version = "1.0",
            ResourceSpecCharacteristic =
            [
                new() { Name = "interfaceName", ValueType = "string" },
                new() { Name = "interfaceDescription", ValueType = "string" },
                new() { Name = "vlanId", ValueType = "string" },
            ],
            ResourceSpecRelationship = [new() { Id = root.Id, Name = root.Name, RelationshipType = "connected-to" }],
        }, ct);
        await ResourceSpec(new LogicalResourceSpecification
        {
            Name = ResourceSpecs.Bearer,
            Version = "1.0",
            ResourceSpecCharacteristic =
            [
                new() { Name = "vlan", ValueType = "integer" },
                new() { Name = "accessId", ValueType = "string" },
                new() { Name = "uploadSpeed", ValueType = "integer" },
                new() { Name = "downloadSpeed", ValueType = "integer" },
            ],
            ResourceSpecRelationship = [new() { Id = root.Id, Name = root.Name, RelationshipType = "connected-to" }],
        }, ct);
        var cpe = await ResourceSpec(new PhysicalResourceSpecification { Name = ResourceSpecs.Cpe, Version = "1.0", Vendor = "Cisco" }, ct);

        // The Internet specification exposes only the two speeds, each limited to 100, 1000 or 10000 Mbps.
        // Every value entry carries a valueType, because it decides how ordered values are compared.
        List<CharacteristicValueSpecification> Speeds() =>
        [
            new() { ValueType = "integer", Value = 100, UnitOfMeasure = "Mbps", IsDefault = true },
            new() { ValueType = "integer", Value = 1000, UnitOfMeasure = "Mbps" },
            new() { ValueType = "integer", Value = 10000, UnitOfMeasure = "Mbps" },
        ];
        state.InternetSpec = await ServiceSpec(new ServiceSpecification_Create
        {
            Name = ServiceSpecs.Internet,
            Version = "1.0",
            LifecycleStatus = "Launched",
            SpecCharacteristic =
            [
                new() { Name = Characteristics.BandwidthDown, ValueType = "integer", CharacteristicValueSpecification = Speeds() },
                new() { Name = Characteristics.BandwidthUp, ValueType = "integer", CharacteristicValueSpecification = Speeds() },
            ],
            ResourceSpecification = [new() { Id = root.Id!, Name = root.Name, ReferredType = "LogicalResourceSpecification" }],
        }, ct);

        // The device specification's characteristics have no default, so every order for it must supply them.
        state.DeviceSpec = await ServiceSpec(new ServiceSpecification_Create
        {
            Name = ServiceSpecs.Device,
            Version = "1.0",
            LifecycleStatus = "Launched",
            SpecCharacteristic =
            [
                new() { Name = Characteristics.SerialNumber, ValueType = "string", CharacteristicValueSpecification = [new() { ValueType = "string" }] },
                new() { Name = Characteristics.Model, ValueType = "string", CharacteristicValueSpecification = [new() { ValueType = "string" }] },
            ],
            ResourceSpecification = [new() { Id = cpe.Id!, Name = cpe.Name, ReferredType = "PhysicalResourceSpecification" }],
        }, ct);

        // Categories first, then the catalog that lists them, then one candidate per specification.
        var internet = await Category("Internet", ct);
        var device = await Category("Device", ct);
        if ((await serviceCatalog.ListServiceCatalogsAsync(new() { Name = "B2B Services" }, ct)).Items.Length == 0)
            await serviceCatalog.CreateServiceCatalogAsync(new ServiceCatalog_Create
            {
                Name = "B2B Services",
                Category = [new() { Id = internet }, new() { Id = device }],
            }, ct);
        await Candidate("B2B Internet", state.InternetSpec, internet, ct);
        await Candidate("B2B Device", state.DeviceSpec, device, ct);
        Step("Catalog", "B2B Services");
    }

    private async Task<ResourceSpecification> ResourceSpec(ResourceSpecification spec, CancellationToken ct)
    {
        var found = (await resourceCatalog.ListResourceSpecificationsAsync(new() { Name = spec.Name }, ct)).Items.FirstOrDefault()
            ?? await resourceCatalog.CreateResourceSpecificationAsync(spec, ct);
        Step("Resource specification", $"{found.Name} {found.Id}");
        return found;
    }

    private async Task<string> ServiceSpec(ServiceSpecification_Create spec, CancellationToken ct)
    {
        var id = (await serviceCatalog.ListServiceSpecificationsAsync(new() { Name = spec.Name }, ct)).Items.FirstOrDefault()?.Id
            ?? (await serviceCatalog.CreateServiceSpecificationAsync(spec, ct)).Id!;
        Step("Service specification", $"{spec.Name} {id}");
        return id;
    }

    private async Task<string> Category(string name, CancellationToken ct) =>
        (await serviceCatalog.ListServiceCategoriesAsync(new() { Name = name }, ct)).Items.FirstOrDefault()?.Id
        ?? (await serviceCatalog.CreateServiceCategoryAsync(new ServiceCategory_Create { Name = name, IsRoot = true }, ct)).Id!;

    private async Task Candidate(string name, string specId, string categoryId, CancellationToken ct)
    {
        if ((await serviceCatalog.ListServiceCandidatesAsync(new() { Name = name }, ct)).Items.Length == 0)
            await serviceCatalog.CreateServiceCandidateAsync(new ServiceCandidate_Create
            {
                Name = name,
                ServiceSpecification = new() { Id = specId },
                Category = [new() { Id = categoryId }],
            }, ct);
    }

    // Step 3 (the last part): map the workflows the worker application registered to the two specifications.
    // A map names seven workflows per specification and action; unused slots get CxO's generic no-op workflows.
    private async Task MapTheWorkflows(State state, CancellationToken ct)
    {
        await Map(state.InternetSpec!, ServiceAction.Add, preparation: Workflows.InternetPrepare, check: Workflows.InternetCheck,
            fulfillment: Workflows.InternetFulfill, fulfillmentRollback: Workflows.Release, ct: ct);
        // A failed change must not tear down a working service, so a Modify keeps the generic fulfillment rollback.
        await Map(state.InternetSpec!, ServiceAction.Modify, preparation: Workflows.InternetPrepare, check: Workflows.InternetCheck,
            fulfillment: Workflows.InternetFulfill, ct: ct);
        await Map(state.InternetSpec!, ServiceAction.Delete, fulfillment: Workflows.Release, ct: ct);
        await Map(state.DeviceSpec!, ServiceAction.Add, fulfillment: Workflows.CpeFulfill, fulfillmentRollback: Workflows.Release, ct: ct);
        await Map(state.DeviceSpec!, ServiceAction.Modify, ct: ct);
        await Map(state.DeviceSpec!, ServiceAction.Delete, fulfillment: Workflows.Release, ct: ct);

        // The activation workflow used by TMF640 in step 7.
        var existing = (await activationEngine.ListWorkflowMappingsAsync(
            new ListWorkflowMappingsQuery { ServiceSpecificationId = state.InternetSpec }, ct)).Mappings.FirstOrDefault();
        if (existing is null)
            await activationEngine.CreateWorkflowMappingAsync(new CreateWorkflowMappingRequest
            {
                ServiceSpecificationId = state.InternetSpec,
                WorkflowName = Workflows.Activate,
                WorkflowVersion = 1,
            }, ct);
        Step("Activation workflow map", $"{ServiceSpecs.Internet} -> {Workflows.Activate}");
    }

    private async Task Map(string specId, ServiceAction action,
        string preparation = Workflows.GenericPrepare, string check = Workflows.GenericCheck,
        string fulfillment = Workflows.GenericFulfill, string fulfillmentRollback = Workflows.GenericFulfillRollback,
        CancellationToken ct = default)
    {
        static ServiceWorkflow W(string name) => new() { Name = name, Version = 1 };
        var stages = new StageMap
        {
            PreparationWorkflow = W(preparation),
            FeasibilityCheckWorkflow = W(check),
            FulfillmentWorkflow = W(fulfillment),
            PreparationRollbackWorkflow = W(Workflows.GenericPrepareRollback),
            FeasibilityCheckRollbackWorkflow = W(Workflows.GenericCheckRollback),
            FulfillmentRollbackWorkflow = W(fulfillmentRollback),
            CancellationWorkflow = W(Workflows.GenericCancel),
        };

        // Find this action's map among the specification's maps.
        var existing = (await orderingEngine.ListServiceWorkflowMapsAsync(
            new ListServiceWorkflowsQuery { ServiceSpecificationId = specId }, ct)).Mappings.FirstOrDefault(m => m.Action == action);
        if (existing is null)
            await orderingEngine.CreateServiceWorkflowMapAsync(
                new CreateServiceWorkflowMapping { ServiceSpecificationId = specId, Action = action, StageMap = stages }, ct);
        else
            await orderingEngine.UpdateServiceWorkflowMapAsync(
                new UpdateServiceWorkflowMapping { Id = existing.Id, ServiceSpecificationId = specId, Action = action, StageMap = stages }, ct);
        Step("Service workflow map", $"{specId} {action}: {preparation} / {check} / {fulfillment}");
    }

    private static void Step(string what, string? value) => Console.WriteLine($"  {what,-24} {value}");
}

/// <summary>Setup found something in the environment it must not change.</summary>
public class SetupConflictException(string message) : Exception(message);
