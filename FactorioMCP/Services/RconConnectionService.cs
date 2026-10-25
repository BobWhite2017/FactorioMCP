using FactorioMCP.Rcon;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FactorioMCP.Services;

/// <summary>
/// Hosted service that connects and authenticates the RCON client on application startup.
/// Reads connection settings from environment variables.
/// Retries connection with exponential backoff if the server is not yet available.
/// After connecting, initializes game-level listeners (e.g. chat message capture).
/// </summary>
internal sealed class RconConnectionService(
    RconClient rcon,
    FactorioService factorioService,
    IConfiguration configuration,
    ILogger<RconConnectionService> logger) : IHostedService
{
    private const int MaxStartupAttempts = 5;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var host = configuration["FACTORIO_RCON_HOST"] ?? "127.0.0.1";
        var port = int.TryParse(configuration["FACTORIO_RCON_PORT"], out var p) ? p : 27015;
        var password = configuration["FACTORIO_RCON_PASSWORD"] ?? "mypassword";

        rcon.AcquireControlLease(host, port);
        logger.LogInformation("Process {ProcessId} acquired exclusive local MCP control of {Host}:{Port}",
            Environment.ProcessId, host, port);

        var delay = InitialBackoff;
        for (var attempt = 1; attempt <= MaxStartupAttempts; attempt++)
        {
            try
            {
                await rcon.ConnectAndAuthenticateAsync(host, port, password, cancellationToken);
                logger.LogInformation("RCON connected to {Host}:{Port}", host, port);

                break;
            }
            catch (Exception ex) when (attempt < MaxStartupAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "RCON connection attempt {Attempt}/{Max} failed. Retrying in {Delay}s...",
                    attempt, MaxStartupAttempts, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }

        await InitializeGameListenersAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task InitializeGameListenersAsync(CancellationToken cancellationToken)
    {
        const int maxProbeAttempts = 3;
        for (var attempt = 1; attempt <= maxProbeAttempts; attempt++)
        {
            var info = await factorioService.GetBridgeStatusAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(info))
            {
                ValidateBridgeResponse(info, "ready");
                break;
            }

            if (attempt == maxProbeAttempts)
            {
                throw new InvalidOperationException(
                    $"Factorio returned an empty bridge capability response on all {maxProbeAttempts} attempts. Check the test server's RCON/Lua command readiness and logs.");
            }

            logger.LogWarning(
                "Empty RCON response to read-only bridge capability probe {Attempt}/{Max}. Retrying in 250ms; no mutating command has been retried",
                attempt, maxProbeAttempts);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        var response = await factorioService.InitializeChatListenerAsync(cancellationToken);
        ValidateBridgeResponse(response);
        logger.LogInformation("Factorio MCP bridge API 1 initialized");
    }

    internal static void ValidateBridgeResponse(string response, string expectedStatus = "initialized")
    {
        const string message = "Factorio MCP bridge API 1 is required. Install and enable factorio-mcp-bridge 0.1.0 on the server and every client, then reload the test save.";
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("bridge_api_version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var apiVersion) || apiVersion != 1 ||
                !root.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String ||
                status.GetString() != expectedStatus)
            {
                throw new InvalidOperationException(message);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(message, ex);
        }
    }
}
