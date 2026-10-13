using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Cxo.Clients;

/// <summary>Gets and caches a client-credentials token from Keycloak.</summary>
public sealed class ClientCredentialsTokenSource(IHttpClientFactory httpClients, IOptions<CxoOptions> options)
{
    public const string HttpClientName = "cxo-keycloak";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expires)
            return _token;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expires)
                return _token;

            var cxo = options.Value;
            using var response = await httpClients.CreateClient(HttpClientName).PostAsync(
                cxo.TokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = cxo.ClientId,
                    ["client_secret"] = cxo.ClientSecret,
                }),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Keycloak at {cxo.TokenUrl} refused client '{cxo.ClientId}' ({(int)response.StatusCode}). "
                    + "Check Cxo__ClientId and Cxo__ClientSecret, and that Cxo__TokenUrl names the right realm.");

            var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))!;
            _expires = DateTimeOffset.UtcNow.AddSeconds(token.expires_in - 30);
            return _token = token.access_token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record TokenResponse(string access_token, int expires_in);
}

/// <summary>Adds the bearer token to every call that goes to the TMF API Gateway, and to nothing else.</summary>
public sealed class GatewayTokenHandler(ClientCredentialsTokenSource tokens, IOptions<CxoOptions> options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var gateway = options.Value.GatewayUrl.TrimEnd('/') + "/";
        if (request.RequestUri?.AbsoluteUri.StartsWith(gateway, StringComparison.OrdinalIgnoreCase) == true)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}
