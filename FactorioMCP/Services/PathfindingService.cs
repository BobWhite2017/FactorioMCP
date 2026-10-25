using System.Globalization;
using System.Text;
using System.Text.Json;
using FactorioMCP.Rcon;

namespace FactorioMCP.Services;

/// <summary>
/// Pathfinding service using Factorio's A* pathfinder with C#-driven waypoint following.
/// 
/// Design: All navigation logic runs in C#, with Lua only used for:
///   1. Requesting paths (async via request_path)
///   2. Receiving path results through the persistent bridge mod
///   3. Continuous walking via the bridge's on_tick handler
/// In Factorio 2, walking_state only persists for a single tick.
/// </summary>
internal sealed class PathfindingService(RconClient rcon)
{
    /// <summary>
    /// Poll interval for position checks during walking. Can be overridden in tests.
    /// </summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(25);

    private int? _lastDirection;

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Walk to a target position using Factorio's A* pathfinder.
    /// Returns a structured JSON result with status (arrived/stuck/timeout/no_path),
    /// final position, and distance.
    /// </summary>
    public async Task<string> WalkToAsync(
        double targetX, double targetY,
        double tolerance,
        double timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be positive and finite.");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);

        // Check if already at target
        var (px, py) = await GetPositionAsync(cancellationToken);
        var dist = Distance(px, py, targetX, targetY);

        if (dist <= tolerance)
            return FormatResult("arrived", px, py, targetX, targetY, dist, tolerance);

        // Request path from Factorio's A* pathfinder
        var requestId = await RequestPathAsync(targetX, targetY, tolerance, cancellationToken);
        if (requestId < 0)
            return FormatResult("no_character", px, py, targetX, targetY, dist, tolerance);

        // Wait for path computation (usually 1-2 game ticks)
        List<(double x, double y)>? waypoints = null;
        while (DateTime.UtcNow < deadline && waypoints is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            if (DateTime.UtcNow >= deadline) break;
            var (status, path) = await GetPathResultAsync(requestId, cancellationToken);

            if (status == "ok" && path is not null)
            {
                waypoints = path;
                break;
            }
            if (status == "no_path")
                return FormatResult("no_path", px, py, targetX, targetY, dist, tolerance);
            if (status == "busy")
            {
                // Pathfinder overloaded — old request already cleaned up by
                // GetPathResultAsync (non-ok entries are nil'd), just retry
                var retryDelay = deadline - DateTime.UtcNow;
                if (retryDelay <= TimeSpan.Zero) break;
                await Task.Delay(retryDelay < TimeSpan.FromSeconds(1) ? retryDelay : TimeSpan.FromSeconds(1),
                    cancellationToken);
                if (DateTime.UtcNow >= deadline) break;
                requestId = await RequestPathAsync(targetX, targetY, tolerance, cancellationToken);
                if (requestId < 0)
                    return FormatResult("no_character", px, py, targetX, targetY, dist, tolerance);
            }
        }

        if (waypoints is null)
            return WithDiagnostics(
                FormatResult("timeout", px, py, targetX, targetY, dist, tolerance),
                "path_request", await GetNavigationDiagnosticsAsync(cancellationToken));

        if (waypoints.Count == 0)
            return FormatResult("no_path", px, py, targetX, targetY, dist, tolerance);

        waypoints.Insert(0, (px, py));

        // Draw debug visualization
        await DrawPathAsync(waypoints, tolerance, cancellationToken);

        // Follow waypoints from C#
        return await FollowWaypointsAsync(waypoints, targetX, targetY, tolerance, deadline, cancellationToken);
    }

    /// <summary>
    /// Get the player's current position.
    /// </summary>
    public Task<string> GetPlayerPositionAsync(CancellationToken cancellationToken = default)
    {
        return rcon.ExecuteLuaAsync("""
            local p = game.connected_players[1].position
            rcon.print('{"x":'..p.x..',"y":'..p.y..'}')
            """, cancellationToken);
    }

    // ── Waypoint Following (Segment-Vector Projection Model) ──────────

    private async Task<string> FollowWaypointsAsync(
        List<(double x, double y)> waypoints,
        double targetX, double targetY,
        double tolerance,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        // Segment index: player walks from waypoints[segIndex] toward waypoints[segIndex+1].
        // When projection along the segment passes the endpoint, advance to next segment.
        int segIndex = 0;
        double bestProgress = 0; // cumulative distance along path (for stuck detection)
        int stuckCount = 0;
        const int maxStuckCount = 60;
        long? stationarySinceTick = null;

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (px, py) = await GetPositionAsync(cancellationToken);

                // Check if we've reached the final destination
                var dist = Distance(px, py, targetX, targetY);
                if (dist <= tolerance)
                {
                    return FormatResult("arrived", px, py, targetX, targetY, dist, tolerance);
                }

                // Advance segments using projection
                segIndex = Math.Min(AdvanceSegment(waypoints, segIndex, px, py), waypoints.Count - 2);

                // Stuck detection: measure progress along the path, not toward the goal.
                // Progress = sum of completed segment lengths + projection on current segment.
                var progress = GetPathProgress(waypoints, segIndex, px, py);
                if (progress > bestProgress + 0.1)
                {
                    bestProgress = progress;
                    stuckCount = 0;
                    stationarySinceTick = null;
                }
                else
                {
                    stuckCount++;
                }

                if (stuckCount > maxStuckCount)
                {
                    var diagnostics = await GetNavigationDiagnosticsAsync(cancellationToken);
                    var tick = diagnostics.GetProperty("tick").GetInt64();
                    stationarySinceTick ??= tick;
                    // RCON polls can run while simulation ticks are paused or delayed.
                    if (tick - stationarySinceTick.Value >= 180)
                    {
                        return WithDiagnostics(
                            FormatResult("stuck", px, py, targetX, targetY, dist, tolerance),
                            "walking", diagnostics);
                    }
                }

                // Steer toward the endpoint of the current segment
                var (wpx, wpy) = waypoints[segIndex + 1];
                var direction = CalculateDirection(px, py, wpx, wpy, _lastDirection);
                if (_lastDirection != direction)
                    await SetWalkingDirectionAsync(direction, cancellationToken);

                await Task.Delay(PollInterval, cancellationToken);
            }

            var timeoutDiagnostics = await GetNavigationDiagnosticsAsync(cancellationToken);
            var (finalX, finalY) = await GetPositionAsync(cancellationToken);
            var finalDist = Distance(finalX, finalY, targetX, targetY);
            return WithDiagnostics(
                FormatResult("timeout", finalX, finalY, targetX, targetY, finalDist, tolerance),
                "walking", timeoutDiagnostics);
        }
        finally
        {
            await StopWalkingAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Advance the segment index by projecting the player position onto the current segment vector.
    /// When the projection parameter t >= 1.0 (player has passed the segment endpoint),
    /// advance to the next segment. Only advances one segment at a time to maintain path fidelity
    /// and avoid skipping obstacle-avoidance waypoints.
    /// </summary>
    internal static int AdvanceSegment(
        List<(double x, double y)> waypoints, int segIndex,
        double playerX, double playerY)
    {
        while (segIndex < waypoints.Count - 1)
        {
            var t = ProjectOntoSegment(waypoints, segIndex, playerX, playerY);

            // Player hasn't passed the segment endpoint yet — stay on this segment
            if (t < 1.0)
                break;

            segIndex++;
        }

        return segIndex;
    }

    /// <summary>
    /// Calculate cumulative progress along the path in tiles.
    /// Sum of completed segment lengths + clamped projection on current segment.
    /// Used for stuck detection: if this value stops increasing, the player is stuck.
    /// </summary>
    internal static double GetPathProgress(
        List<(double x, double y)> waypoints, int segIndex,
        double playerX, double playerY)
    {
        double progress = 0;

        // Sum completed segments
        for (int i = 0; i < segIndex && i < waypoints.Count - 1; i++)
        {
            var (ax, ay) = waypoints[i];
            var (bx, by) = waypoints[i + 1];
            progress += Distance(ax, ay, bx, by);
        }

        // Add projection on current segment (clamped to [0, segLen])
        if (segIndex < waypoints.Count - 1)
        {
            var (sx, sy) = waypoints[segIndex];
            var (ex, ey) = waypoints[segIndex + 1];
            var segLen = Distance(sx, sy, ex, ey);

            if (segLen > 0.001)
            {
                var t = ProjectOntoSegment(waypoints, segIndex, playerX, playerY);
                progress += Math.Clamp(t, 0, 1) * segLen;
            }
        }

        return progress;
    }

    /// <summary>
    /// Project the player position onto a path segment, returning the parameter t.
    /// t=0 means at segment start, t=1 means at segment end, t>1 means past the endpoint.
    /// Uses the standard vector projection formula: t = dot(player-start, end-start) / |end-start|².
    /// </summary>
    internal static double ProjectOntoSegment(
        List<(double x, double y)> waypoints, int segIndex,
        double playerX, double playerY)
    {
        var (sx, sy) = waypoints[segIndex];
        var (ex, ey) = waypoints[segIndex + 1];
        var segX = ex - sx;
        var segY = ey - sy;
        var segLenSq = segX * segX + segY * segY;

        if (segLenSq < 0.001)
            return 1.0; // degenerate segment, treat as passed

        var toPlayerX = playerX - sx;
        var toPlayerY = playerY - sy;
        return (segX * toPlayerX + segY * toPlayerY) / segLenSq;
    }

    /// <summary>
    /// Calculate Factorio 2 direction (0-15, even values for 8 cardinals+diagonals) with optional
    /// hysteresis to prevent oscillation at sector boundaries during straight-line movement.
    /// Factorio 2 uses 16 directions: 0=N 2=NE 4=E 6=SE 8=S 10=SW 12=W 14=NW
    /// (odd values are intermediate directions like NNE=1, ENE=3, etc.)
    ///
    /// When <paramref name="previousDirection"/> is provided, a dead zone (~7.5°) around each
    /// sector boundary prevents direction changes from small lateral drift. The direction only
    /// switches when the angle moves clearly past the boundary.
    /// </summary>
    internal static int CalculateDirection(double fromX, double fromY, double toX, double toY, int? previousDirection = null)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        var adx = Math.Abs(dx);
        var ady = Math.Abs(dy);

        const double threshold = 2.414; // tan(67.5°) — standard sector boundary

        var newDirection = ComputeOctant(dx, dy, adx, ady, threshold);

        if (previousDirection is not { } prev || newDirection == prev)
            return newDirection;

        // Apply hysteresis: require the angle to move further past the boundary before switching.
        // Cardinal directions (N/E/S/W) use a relaxed threshold making it harder to leave cardinal.
        // Diagonal directions (NE/SE/SW/NW) use a strict threshold making it harder to leave diagonal.
        var hysteresisThreshold = prev % 4 == 0
            ? 1.732  // tan(60°) — ~7.5° dead zone keeping cardinal
            : 3.732; // tan(75°) — ~7.5° dead zone keeping diagonal

        var hysteresisDirection = ComputeOctant(dx, dy, adx, ady, hysteresisThreshold);
        return hysteresisDirection == prev ? prev : newDirection;
    }

    /// <summary>
    /// Map a direction vector to one of 8 octant directions using the given cardinal/diagonal threshold.
    /// Higher threshold = narrower cardinal zones (harder to be cardinal, easier to be diagonal).
    /// </summary>
    private static int ComputeOctant(double dx, double dy, double adx, double ady, double threshold)
    {
        if (ady > adx * threshold)
            return dy < 0 ? 0 : 8; // North or South

        if (adx > ady * threshold)
            return dx > 0 ? 4 : 12; // East or West

        // Diagonal
        if (dx > 0)
            return dy < 0 ? 2 : 6; // NE or SE

        return dy < 0 ? 14 : 10; // NW or SW
    }

    // ── Lua Commands ────────────────────────────────────────────────

    private async Task<JsonElement> GetNavigationDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var json = await rcon.ExecuteLuaAsync("""
            local p = game.connected_players[1]
            local navigation_diagnostics = {
                tick = game.tick, speed = game.speed,
                player_index = p.index, surface_index = p.surface.index,
                x = p.position.x, y = p.position.y,
                walking = p.walking_state.walking, direction = p.walking_state.direction
            }
            rcon.print(helpers.table_to_json(navigation_diagnostics))
            """, cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string WithDiagnostics(string result, string phase, JsonElement diagnostics) =>
        result[..^1] + ",\"phase\":" + JsonSerializer.Serialize(phase) +
        ",\"diagnostics\":" + diagnostics.GetRawText() + "}";

    /// <summary>
    /// Request a path from Factorio's A* pathfinder.
    /// </summary>
    private async Task<int> RequestPathAsync(
        double targetX, double targetY, double tolerance,
        CancellationToken cancellationToken)
    {
        var lua = string.Create(CultureInfo.InvariantCulture, $$"""
            local p = game.connected_players[1]
            if not p.character or not p.character.valid then
                rcon.print('-1')
                return
            end
            local id = remote.call("factorio_mcp_bridge", "request_path", {{targetX}}, {{targetY}}, {{tolerance}})
            rcon.print(tostring(id))
            """);

        var result = await rcon.ExecuteLuaAsync(lua, cancellationToken);
        return int.TryParse(result.Trim(), out var id) ? id : -1;
    }

    /// <summary>
    /// Get the result of a path request.
    /// </summary>
    private async Task<(string status, List<(double x, double y)>? path)> GetPathResultAsync(
        int requestId, CancellationToken cancellationToken)
    {
        var lua = string.Create(CultureInfo.InvariantCulture, $$"""
            local r = remote.call("factorio_mcp_bridge", "take_path_result", {{requestId}})
            if not r then
                rcon.print('{"status":"waiting"}')
                return
            end
            if r.status ~= "ok" then
                rcon.print('{"status":"'..r.status..'"}')
                return
            end
            local pts = {}
            for i, wp in ipairs(r.path) do
                pts[i] = '{"x":'..wp.position.x..',"y":'..wp.position.y..'}'
            end
            rcon.print('{"status":"ok","path":['..table.concat(pts, ',')..']}')
            """);

        var result = await rcon.ExecuteLuaAsync(lua, cancellationToken);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        var status = root.GetProperty("status").GetString() ?? "unknown";

        if (status != "ok" || !root.TryGetProperty("path", out var pathProp))
            return (status, null);

        var waypoints = new List<(double, double)>(pathProp.GetArrayLength());
        foreach (var wp in pathProp.EnumerateArray())
            waypoints.Add((wp.GetProperty("x").GetDouble(), wp.GetProperty("y").GetDouble()));

        return (status, waypoints);
    }

    private async Task<(double x, double y)> GetPositionAsync(CancellationToken cancellationToken)
    {
        var json = await GetPlayerPositionAsync(cancellationToken);
        return ParsePosition(json);
    }

    private async Task StopWalkingAsync(CancellationToken cancellationToken)
    {
        _lastDirection = null;
        var response = await rcon.ExecuteLuaAsync("""
            remote.call("factorio_mcp_bridge", "set_walking", nil)
            game.connected_players[1].walking_state = {walking = false, direction = defines.direction.north}
            rcon.print('ok')
            """, cancellationToken);
        if (response.Trim() != "ok")
            throw new InvalidOperationException($"Failed to stop walking; bridge control may still be active: {response}");
    }

    private async Task SetWalkingDirectionAsync(int direction, CancellationToken cancellationToken)
    {
        var response = await rcon.ExecuteLuaAsync(string.Create(CultureInfo.InvariantCulture, $$"""
            remote.call("factorio_mcp_bridge", "set_walking", {{direction}})
            rcon.print('ok')
            """), cancellationToken);
        if (response.Trim() != "ok")
            throw new InvalidOperationException($"Failed to set walking direction: {response}");
        _lastDirection = direction;
    }

    /// <summary>
    /// Draw debug path visualization on the game map.
    /// Draws lines from the player's current position through all waypoints,
    /// plus a circle at the final waypoint showing the arrival tolerance.
    /// </summary>
    private Task DrawPathAsync(List<(double x, double y)> waypoints, double tolerance, CancellationToken cancellationToken)
    {
        if (waypoints.Count == 0)
            return Task.CompletedTask;

        var lua = new StringBuilder();
        lua.Append("local p = game.connected_players[1]\n");
        lua.Append("local s = p.surface\n");

        // Draw first segment from the player's actual position to the first waypoint
        var (wx0, wy0) = waypoints[0];
        lua.AppendFormat(CultureInfo.InvariantCulture,
            "rendering.draw_line{{color={{0,1,0.3,0.7}},width=2,from=p.position,to={{{0},{1}}},surface=s,time_to_live=1800,draw_on_ground=true}}\n",
            wx0, wy0);

        // Draw remaining segments between waypoints
        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            var (x1, y1) = waypoints[i];
            var (x2, y2) = waypoints[i + 1];
            lua.AppendFormat(CultureInfo.InvariantCulture,
                "rendering.draw_line{{color={{0,1,0.3,0.7}},width=2,from={{{0},{1}}},to={{{2},{3}}},surface=s,time_to_live=1800,draw_on_ground=true}}\n",
                x1, y1, x2, y2);
        }

        var (lastX, lastY) = waypoints[^1];
        lua.AppendFormat(CultureInfo.InvariantCulture,
            "rendering.draw_circle{{color={{0,1,0,0.5}},radius={0},filled=false,width=2,target={{{1},{2}}},surface=s,time_to_live=1800,draw_on_ground=true}}\n",
            tolerance, lastX, lastY);
        lua.Append("rcon.print('ok')");

        return rcon.ExecuteLuaAsync(lua.ToString(), cancellationToken);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    internal static (double x, double y) ParsePosition(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble());
    }

    internal static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static string FormatResult(
        string status, double x, double y,
        double targetX, double targetY, double distance, double tolerance)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $$"""{"status":"{{status}}","x":{{x}},"y":{{y}},"target_x":{{targetX}},"target_y":{{targetY}},"distance":{{string.Format(CultureInfo.InvariantCulture, "{0:F2}", distance)}},"tolerance":{{tolerance}}}""");
    }
}
