using System.Net;
using ApiClientCommons.Exceptions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace Acme.Cli;

/// <summary>Turns the failures a new environment typically produces into one line that says what to check.</summary>
static class Errors
{
    public static int Report(Exception error, IConfiguration settings)
    {
        var message = error switch
        {
            SetupConflictException or UserError => error.Message,

            ErrorResponseException { StatusCode: HttpStatusCode.Unauthorized } =>
                $"The gateway at {settings["Cxo:GatewayUrl"]} refused the token (401). Cxo__TokenUrl must be the Keycloak address the gateway trusts.",
            ErrorResponseException { StatusCode: HttpStatusCode.Forbidden } =>
                $"The gateway refused the call (403). The Keycloak client {settings["Cxo:ClientId"]} lacks a role it needs; run 'acme keycloak-client' or ask your Keycloak administrator.",
            ErrorResponseException e =>
                $"CxO answered {(int)e.StatusCode}: {e.GetReason() ?? e.Message}",

            HttpRequestException e =>
                $"Cannot reach CxO: {e.Message}. Check the addresses in the README's settings table.",
            RpcException e when e.StatusCode is StatusCode.Unavailable =>
                $"Cannot reach a CxO engine: {e.Status.Detail} Check ServiceOrderingEngine__BaseUrl and ServiceActivationEngine__BaseUrl.",
            RpcException e => $"A CxO engine answered {e.StatusCode}: {e.Status.Detail}",

            TimeoutException or InvalidOperationException => error.Message,
            _ => $"{error.GetType().Name}: {error.Message}",
        };
        Console.Error.WriteLine($"acme: {message}");
        return 1;
    }
}

/// <summary>A mistake in how the CLI was run or set up, reported as its message alone.</summary>
public class UserError(string message) : Exception(message);
