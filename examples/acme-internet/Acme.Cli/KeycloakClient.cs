using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Acme.Cli;

/// <summary>
/// Creates the Keycloak client the example calls CxO with, as "Before you start" describes:
/// a confidential client with service accounts, the gateway_client_roles default scope (without it every call
/// gets 403), and the cxo-tmf-api-gateway roles &lt;service&gt;.read and &lt;service&gt;.modify for every API
/// the example calls. Uses Keycloak's admin API; an existing client is reused. Returns the client secret.
/// </summary>
public class KeycloakClient(IHttpClientFactory httpClients, IConfiguration configuration)
{
    private static readonly string[] Services =
    [
        "cxo-party-management", "cxo-customer-management",
        "cxo-geographic-address-management", "cxo-geographic-site-management",
        "cxo-resource-catalog-management", "cxo-service-catalog-management",
        "cxo-service-order-management", "cxo-service-inventory-management", "cxo-resource-inventory-management",
        "cxo-resource-order-management",
        "cxo-service-activation-management",
    ];

    public async Task<string> Create(string clientId, CancellationToken ct)
    {
        var keycloak = configuration.GetSection("Keycloak");
        var url = keycloak["Url"]!.TrimEnd('/');
        if (string.IsNullOrEmpty(keycloak["AdminPassword"]))
            throw new UserError("Creating the client needs a Keycloak administrator: set Keycloak__AdminUser and Keycloak__AdminPassword.");
        try
        {
            return await Create(clientId, keycloak, url, ct);
        }
        catch (HttpRequestException e)
        {
            // Without this, a Keycloak failure would read like a failure to reach CxO.
            throw new UserError($"Keycloak at {url}: {e.Message.TrimEnd('.')}. Check Keycloak__Url and Keycloak__Realm, and that {keycloak["AdminUser"]} administers the realm.");
        }
    }

    private async Task<string> Create(string clientId, IConfigurationSection keycloak, string url, CancellationToken ct)
    {
        var http = httpClients.CreateClient();

        var login = await http.PostAsync($"{url}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = keycloak["AdminUser"]!,
                ["password"] = keycloak["AdminPassword"]!,
            }), ct);
        if (!login.IsSuccessStatusCode)
            throw new UserError($"Keycloak at {url} refused the administrator sign-in ({(int)login.StatusCode}). Check Keycloak__AdminUser and Keycloak__AdminPassword.");
        var token = (string)(await login.Content.ReadFromJsonAsync<JsonObject>(ct))!["access_token"]!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var admin = $"{url}/admin/realms/{keycloak["Realm"]}";

        async Task<JsonArray> List(string path) => (await http.GetFromJsonAsync<JsonArray>($"{admin}/{path}", ct))!;
        async Task Send(HttpMethod method, string path, JsonNode? body = null)
        {
            using var request = new HttpRequestMessage(method, $"{admin}/{path}") { Content = body is null ? null : JsonContent.Create(body) };
            (await http.SendAsync(request, ct)).EnsureSuccessStatusCode();
        }

        // 1. A confidential client with service accounts, and nothing else.
        if ((await List($"clients?clientId={clientId}")).Count == 0)
            await Send(HttpMethod.Post, "clients", new JsonObject
            {
                ["clientId"] = clientId,
                ["publicClient"] = false,
                ["serviceAccountsEnabled"] = true,
                ["standardFlowEnabled"] = false,
                ["directAccessGrantsEnabled"] = false,
            });
        var id = (string)(await List($"clients?clientId={clientId}"))[0]!["id"]!;

        // 2. The scope that puts the gateway roles into the token.
        var scope = (await List("client-scopes")).FirstOrDefault(s => (string?)s!["name"] == "gateway_client_roles")
            ?? throw new UserError($"Realm {keycloak["Realm"]} has no gateway_client_roles client scope. Is Keycloak__Realm the realm CxO uses?");
        await Send(HttpMethod.Put, $"clients/{id}/default-client-scopes/{scope["id"]}");

        // 3. Read and modify roles on every API the example calls.
        var serviceAccount = (await http.GetFromJsonAsync<JsonObject>($"{admin}/clients/{id}/service-account-user", ct))!["id"];
        var gateway = (await List("clients?clientId=cxo-tmf-api-gateway")).FirstOrDefault()?["id"]
            ?? throw new UserError($"Realm {keycloak["Realm"]} has no cxo-tmf-api-gateway client. Is Keycloak__Realm the realm CxO uses?");
        var roles = new JsonArray((await List($"clients/{gateway}/roles?max=500"))
            .Where(r => Services.Contains(((string)r!["name"]!).Replace(".read", "").Replace(".modify", "")))
            .Select(r => r!.DeepClone()).ToArray());
        await Send(HttpMethod.Post, $"users/{serviceAccount}/role-mappings/clients/{gateway}", roles);
        Console.Error.WriteLine($"Client {clientId} in realm {keycloak["Realm"]}: {roles.Count} gateway roles.");

        return (string)(await http.GetFromJsonAsync<JsonObject>($"{admin}/clients/{id}/client-secret", ct))!["value"]!;
    }
}
