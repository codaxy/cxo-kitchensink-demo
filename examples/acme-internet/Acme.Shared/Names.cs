namespace Acme;

/// <summary>The names the catalog, the workers and the workflow maps agree on.</summary>
public static class Names
{
    public static class ServiceSpecs
    {
        public const string Internet = "B2B_InternetServiceSpec";
        public const string Device = "B2B_DeviceSpecification";
    }

    /// <summary>The workers find resource specifications by name, so these must match the catalog.</summary>
    public static class ResourceSpecs
    {
        public const string Root = "L3ServiceResource";
        public const string Interface = "ServiceInterface";
        public const string Bearer = "L2BearerResource";
        public const string Cpe = "Cisco IOS L3 CPE device";
    }

    public static class Characteristics
    {
        public const string BandwidthDown = "internetBandwidthDown";
        public const string BandwidthUp = "internetBandwidthUp";
        public const string SerialNumber = "serialNumber";
        public const string Model = "model";
    }

    public static class Workflows
    {
        public const string InternetPrepare = "acme_internet_prepare";
        public const string InternetCheck = "acme_internet_check";
        public const string InternetFulfill = "acme_internet_fulfill";
        public const string CpeFulfill = "acme_cpe_fulfill";
        public const string Release = "acme_release";
        public const string Activate = "acme_activate";

        // CxO's generic no-op workflows, for the slots the example does not need.
        public const string GenericPrepare = "som_gen_prep";
        public const string GenericCheck = "som_gen_chk_fsblty";
        public const string GenericFulfill = "som_gen_flfll";
        public const string GenericPrepareRollback = "som_gen_prep_rb";
        public const string GenericCheckRollback = "som_gen_chk_fsblty_rb";
        public const string GenericFulfillRollback = "som_gen_flfll_rb";
        public const string GenericCancel = "som_gen_cancel";
    }

    /// <summary>The fault code the feasibility check reports for a speed the access port cannot carry.</summary>
    public const string PortCapacityError = "ACME_PORT_CAPACITY";
}
