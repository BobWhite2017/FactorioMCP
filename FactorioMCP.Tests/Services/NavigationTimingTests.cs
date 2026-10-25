using FactorioMCP.Rcon;
using FactorioMCP.Services;
using Xunit;

namespace FactorioMCP.Tests.Services;

public class NavigationTimingTests
{
    [Fact]
    public async Task RejectedWalkingCommand_FailsExplicitlyAndStops()
    {
        var rcon = new NavigationRcon(controlError: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PathfindingService(rcon).WalkToAsync(10, 0, 1, 1));

        Assert.Contains("Failed to set walking direction", error.Message);
        Assert.True(rcon.Stopped);
    }

    [Fact]
    public async Task RejectedStop_DoesNotReportArrival()
    {
        var rcon = new NavigationRcon(stopError: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PathfindingService(rcon).WalkToAsync(10, 0, 1, 1));

        Assert.Contains("Failed to stop walking", error.Message);
    }

    [Fact]
    public async Task PassingFinalWaypointOutsideTolerance_DoesNotReportArrival()
    {
        var rcon = new NavigationRcon(offPath: true, advancing: true);
        var service = new PathfindingService(rcon) { PollInterval = TimeSpan.FromMilliseconds(1) };

        var result = await service.WalkToAsync(10, 0, 1, 0.8);

        Assert.DoesNotContain("\"status\":\"arrived\"", result);
        Assert.True(rcon.Stopped);
    }

    [Fact]
    public async Task PendingPath_DoesNotReportConfirmedNoPath()
    {
        var rcon = new NavigationRcon(pathPending: true);
        var service = new PathfindingService(rcon);

        var result = await service.WalkToAsync(10, 0, 1, 0.5);

        Assert.Contains("\"status\":\"timeout\"", result);
        Assert.Contains("\"phase\":\"path_request\"", result);
        Assert.Contains("\"tick\":100", result);
        Assert.True(rcon.PathPolls <= 6);
    }

    [Theory]
    [InlineData(false, "timeout")]
    [InlineData(true, "stuck")]
    public async Task StationaryPlayer_RequiresSimulationTicksBeforeReportingStuck(bool advancing, string status)
    {
        var rcon = new NavigationRcon(advancing: advancing);
        var service = new PathfindingService(rcon) { PollInterval = TimeSpan.FromMilliseconds(1) };

        var result = await service.WalkToAsync(10, 0, 1, 0.8);

        Assert.Contains($"\"status\":\"{status}\"", result);
        Assert.Contains("\"phase\":\"walking\"", result);
        Assert.Contains("\"player_index\":1", result);
        Assert.True(rcon.Stopped);
    }

    [Fact]
    public async Task DelayedPath_CanCompleteAfterOldTwentyPollLimit()
    {
        var rcon = new NavigationRcon(delayedPath: true);
        var result = await new PathfindingService(rcon).WalkToAsync(10, 0, 1, 4);

        Assert.Contains("\"status\":\"arrived\"", result);
        Assert.Equal(22, rcon.PathPolls);
        Assert.True(rcon.Stopped);
    }

    [Fact]
    public async Task InvalidDiagnostics_StillStopsWalking()
    {
        var rcon = new NavigationRcon(invalidDiagnostics: true);
        var service = new PathfindingService(rcon) { PollInterval = TimeSpan.FromMilliseconds(1) };

        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(
            () => service.WalkToAsync(10, 0, 1, 0.8));

        Assert.True(rcon.Stopped);
    }

    private sealed class NavigationRcon(
        bool pathPending = false, bool advancing = false,
        bool delayedPath = false, bool invalidDiagnostics = false, bool offPath = false,
        bool controlError = false, bool stopError = false) : RconClient
    {
        public int PathPolls { get; private set; }
        public bool Stopped { get; private set; }
        private int _tick = 100;

        public override Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        {
            string response;
            if (command.Contains("navigation_diagnostics"))
            {
                if (invalidDiagnostics) return Task.FromResult("invalid JSON");
                response = $$"""{"tick":{{_tick}},"player_index":1,"x":0,"y":0,"walking":false,"direction":4,"surface_index":1,"speed":1}""";
                if (advancing) _tick += 60;
            }
            else if (command.Contains("\"request_path\""))
                response = "1";
            else if (command.Contains("\"take_path_result\""))
            {
                PathPolls++;
                response = pathPending || (delayedPath && PathPolls < 22) ? """{"status":"waiting"}"""
                    : """{"status":"ok","path":[{"x":0,"y":0},{"x":10,"y":0}]}""";
            }
            else if (command.Contains("\"set_walking\", nil"))
            {
                Stopped = true;
                response = stopError ? "stop_rejected" : "ok";
            }
            else if (command.Contains("rendering."))
                response = "ok";
            else if (command.Contains("\"set_walking\""))
                response = controlError ? "set_rejected" : "ok";
            else
                response = offPath ? """{"x":20,"y":10}"""
                    : stopError && PathPolls > 0 ? """{"x":10,"y":0}"""
                    : delayedPath && PathPolls >= 22 ? """{"x":10,"y":0}""" : """{"x":0,"y":0}""";
            return Task.FromResult(response);
        }
    }
}
