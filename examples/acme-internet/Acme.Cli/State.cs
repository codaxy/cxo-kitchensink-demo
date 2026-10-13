using System.Text.Json;

namespace Acme.Cli;

/// <summary>
/// The ids each step creates and later steps need, kept in acme-state.json in the working directory
/// so the steps can be run one at a time.
/// </summary>
public class State
{
    private const string FileName = "acme-state.json";

    public string? Organization { get; set; }
    public string? Customer { get; set; }
    public string? Address { get; set; }
    public string? Site { get; set; }
    public string? InternetSpec { get; set; }
    public string? DeviceSpec { get; set; }
    public string? Order { get; set; }
    public string? CpeService { get; set; }
    public string? InternetService { get; set; }

    public static State Load() =>
        File.Exists(FileName) ? JsonSerializer.Deserialize<State>(File.ReadAllText(FileName))! : new State();

    public void Save() =>
        File.WriteAllText(FileName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    public static string Require(string? id, string what, string step) =>
        id ?? throw new InvalidOperationException($"No {what} yet. Run '{step}' first.");
}
