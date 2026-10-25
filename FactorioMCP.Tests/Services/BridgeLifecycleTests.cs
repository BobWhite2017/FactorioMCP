using FactorioMCP.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactorioMCP.Tests.Services;

public class BridgeLifecycleTests
{
    private static RconConnectionService CreateConnectionService(ScriptedRconClient rcon)
    {
        return new RconConnectionService(
            rcon, new FactorioService(rcon), new ConfigurationBuilder().Build(),
            NullLogger<RconConnectionService>.Instance);
    }

    [Fact]
    public async Task Startup_RetriesOnlyEmptyReadOnlyProbesBeforeOneInitialization()
    {
        var rcon = new ScriptedRconClient([
            "", " ",
            """{"status":"ready","bridge_api_version":1}""",
            """{"status":"initialized","bridge_api_version":1}"""
        ]);
        await CreateConnectionService(rcon).InitializeGameListenersAsync(CancellationToken.None);

        Assert.Equal(4, rcon.AllCommands.Count);
        Assert.All(rcon.AllCommands.Take(3), command => Assert.Contains("get_info", command));
        Assert.Contains("\"initialize\"", rcon.AllCommands[3]);
    }

    [Fact]
    public async Task Startup_StopsAfterThreeEmptyProbes()
    {
        var rcon = new ScriptedRconClient([""]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateConnectionService(rcon).InitializeGameListenersAsync(CancellationToken.None));

        Assert.Contains("all 3 attempts", error.Message);
        Assert.Equal(3, rcon.AllCommands.Count);
        Assert.All(rcon.AllCommands, command => Assert.Contains("get_info", command));
    }

    [Theory]
    [InlineData("""{"error":"missing bridge"}""")]
    [InlineData("""{"status":"ready","bridge_api_version":2}""")]
    [InlineData("Lua error")]
    public async Task Startup_DoesNotRetryNonemptyInvalidProbe(string response)
    {
        var rcon = new ScriptedRconClient([response]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateConnectionService(rcon).InitializeGameListenersAsync(CancellationToken.None));
        Assert.Single(rcon.AllCommands);
    }

    [Fact]
    public async Task Startup_DoesNotRetryMutatingInitialization()
    {
        var rcon = new ScriptedRconClient([
            """{"status":"ready","bridge_api_version":1}""", ""
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateConnectionService(rcon).InitializeGameListenersAsync(CancellationToken.None));
        Assert.Equal(2, rcon.AllCommands.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"error":"missing bridge"}""")]
    [InlineData("""{"status":"initialized","bridge_api_version":2}""")]
    public void Startup_RejectsMissingOrIncompatibleBridge(string response)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RconConnectionService.ValidateBridgeResponse(response));
        Assert.Contains("Install and enable factorio-mcp-bridge", error.Message);
    }

    [Fact]
    public void Startup_AcceptsBridgeApiOne()
    {
        RconConnectionService.ValidateBridgeResponse("""{"status":"initialized","bridge_api_version":1}""");
    }

    [Fact]
    public async Task ChatInitialization_UsesPersistentBridgeWithoutRegisteringLevelEvents()
    {
        var rcon = new CapturingRconClient();
        await new FactorioService(rcon).InitializeChatListenerAsync();

        Assert.Contains("remote.call", rcon.LastCommand!);
        Assert.DoesNotContain("script.on_event", rcon.LastCommand!);
        Assert.DoesNotContain("storage.chat_log", rcon.LastCommand!);
    }

    [Fact]
    public async Task Mining_UsesPersistentBridgeWithoutRegisteringLevelEvents()
    {
        var rcon = new CapturingRconClient();
        var service = new MiningService(rcon);
        await service.StartMiningResourceAsync(1, 2);
        await service.StopMiningAsync();

        Assert.All(rcon.AllCommands, command =>
        {
            Assert.Contains("remote.call", command);
            Assert.DoesNotContain("script.on_event", command);
            Assert.DoesNotContain("storage.mine_state", command);
        });
    }

    [Fact]
    public async Task Navigation_UsesPersistentBridgeWithoutRegisteringLevelEvents()
    {
        var rcon = new ScriptedRconClient([
            """{"x":0,"y":0}""",
            "1",
            """{"status":"no_path"}"""
        ]);
        await new PathfindingService(rcon).WalkToAsync(10, 10, 1, 2);

        Assert.Contains(rcon.AllCommands, command => command.Contains("remote.call"));
        Assert.All(rcon.AllCommands, command =>
        {
            Assert.DoesNotContain("script.on_event", command);
            Assert.DoesNotContain("storage.nav_results", command);
        });
    }
}
