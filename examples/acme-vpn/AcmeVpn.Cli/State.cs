using System.Text.Json;

namespace AcmeVpn.Cli;

/// <summary>The ids each step creates and later steps need, kept in vpn-state.json in the working directory.</summary>
public class State
{
    private const string FileName = "vpn-state.json";

    public string? Customer { get; set; }
    public Dictionary<string, string> Sites { get; set; } = [];
    public string? VpnSpec { get; set; }
    public string? SiteSpec { get; set; }
    public string? Order { get; set; }
    public string? VpnService { get; set; }
    public Dictionary<string, string> SiteServices { get; set; } = [];

    public static State Load() =>
        File.Exists(FileName) ? JsonSerializer.Deserialize<State>(File.ReadAllText(FileName))! : new State();

    public void Save() =>
        File.WriteAllText(FileName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    public static string Require(string? id, string what, string step) =>
        id ?? throw new UserError($"No {what} yet. Run 'vpn {step}' first.");
}

/// <summary>A mistake in how the CLI was run or set up, reported as its message alone.</summary>
public class UserError(string message) : Exception(message);

/// <summary>Setup found something in the environment it must not change.</summary>
public class SetupConflictException(string message) : Exception(message);
