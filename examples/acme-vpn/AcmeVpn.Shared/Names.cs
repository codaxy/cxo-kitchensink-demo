namespace AcmeVpn;

/// <summary>The names the catalog, the workers and the workflow maps agree on.</summary>
public static class Names
{
    public static class ServiceSpecs
    {
        public const string Vpn = "AcmeL3VpnServiceSpec";
        public const string Site = "AcmeL3VpnSiteServiceSpec";
    }

    /// <summary>The workers find resource specifications by name, so these must match the catalog.</summary>
    public static class ResourceSpecs
    {
        public const string Instance = "AcmeL3VpnInstance";
        public const string Attachment = "AcmeL3VpnAttachment";
        public const string CpeInterface = "AcmeL3VpnCpeInterface";
        public const string CpeDevice = "AcmeVpnCpeDevice";
    }

    public static class Characteristics
    {
        public const string VpnName = "vpnName";
        public const string Bandwidth = "bandwidth";
        public const string CpeSerialNumber = "cpeSerialNumber";
    }

    public static class Workflows
    {
        // Service workflows, run by the service ordering engine.
        public const string VpnPrepare = "acme_vpn_prepare";
        public const string VpnFulfill = "acme_vpn_fulfill";
        public const string VpnRelease = "acme_vpn_release";
        public const string SitePrepare = "acme_vpn_site_prepare";
        public const string SiteFulfill = "acme_vpn_site_fulfill";
        public const string SiteRelease = "acme_vpn_site_release";

        // Resource workflows, run by the resource ordering engine.
        public const string CheckCpe = "acme_vpn_check_cpe";
        public const string Configure = "acme_vpn_configure";
        public const string Unconfigure = "acme_vpn_unconfigure";

        // CxO's generic no-op workflows, for the slots the example does not need.
        public const string ServicePrepare = "som_gen_prep";
        public const string ServiceCheck = "som_gen_chk_fsblty";
        public const string ServiceFulfill = "som_gen_flfll";
        public const string ServicePrepareRollback = "som_gen_prep_rb";
        public const string ServiceCheckRollback = "som_gen_chk_fsblty_rb";
        public const string ServiceFulfillRollback = "som_gen_flfll_rb";
        public const string ServiceCancel = "som_gen_cancel";
        public const string ResourcePrepare = "rom_gen_prep";
        public const string ResourceCheck = "rom_gen_chk_fsblty";
        public const string ResourceFulfill = "rom_gen_flfl";
        public const string ResourceCancel = "rom_gen_cancel";
    }

    /// <summary>The fault code a site's resource order reports when its CPE has not been installed yet.</summary>
    public const string CpeNotInstalledError = "ACME_CPE_NOT_INSTALLED";

    /// <summary>The three Acme offices the example connects.</summary>
    public static readonly (string Key, string Name, string City, string CpeSerial)[] Sites =
    [
        ("hq", "Acme HQ", "Exampletown", "FTX-ACME-HQ"),
        ("north", "Acme Branch North", "Northtown", "FTX-ACME-NORTH"),
        ("south", "Acme Branch South", "Southtown", "FTX-ACME-SOUTH"),
    ];
}
