using System.Globalization;
using System.Text;

namespace FactorioMCP.Services;

internal sealed partial class FactorioService
{
    // ── Chat ─────────────────────────────────────────────────────────

    public Task<string> GetBridgeStatusAsync(CancellationToken cancellationToken = default)
    {
        return rcon.ExecuteLuaAsync("""
            if not remote.interfaces.factorio_mcp_bridge then
                rcon.print('{"error":"Install and enable factorio-mcp-bridge 0.1.0 on the server and all clients, then reload the test save."}')
                return
            end
            rcon.print(helpers.table_to_json(remote.call("factorio_mcp_bridge", "get_info")))
            """, cancellationToken);
    }

    /// <summary>
    /// Initialize the persistent bridge session without registering scenario events.
    /// Existing chat messages are preserved.
    /// </summary>
    public Task<string> InitializeChatListenerAsync(CancellationToken cancellationToken = default)
    {
        return rcon.ExecuteLuaAsync("""
            if not remote.interfaces.factorio_mcp_bridge then
                rcon.print('{"error":"Install and enable factorio-mcp-bridge 0.1.0 on the server and all clients, then reload the test save."}')
                return
            end
            local result = remote.call("factorio_mcp_bridge", "initialize")
            rcon.print(helpers.table_to_json(result))
            """, cancellationToken);
    }

    /// <summary>
    /// Read chat messages from the stored log. Optionally filters to messages
    /// after a given game tick so the caller can poll for new messages only.
    /// </summary>
    public Task<string> GetChatMessagesAsync(long sinceTick = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sinceTick);

        var lua = string.Create(CultureInfo.InvariantCulture, $$"""
            local log = remote.call("factorio_mcp_bridge", "get_chat_log")
            local since_tick = {{sinceTick}}
            local json_escape = function(s)
                return s:gsub('\\', '\\\\'):gsub('"', '\\"'):gsub('\n', '\\n'):gsub('\r', '\\r')
            end
            local parts = {}
            local latest_tick = since_tick
            for _, msg in pairs(log) do
                if msg.tick > since_tick then
                    parts[#parts+1] = '{"tick":'..msg.tick..',"player":"'..json_escape(msg.player_name)..'","message":"'..json_escape(msg.message)..'"}'
                    if msg.tick > latest_tick then latest_tick = msg.tick end
                end
            end
            rcon.print('{"messages":['..table.concat(parts, ",")..'],"count":'..#parts..',"latest_tick":'..latest_tick..'}')
            """);

        return rcon.ExecuteLuaAsync(lua, cancellationToken);
    }

    /// <summary>
    /// Send a chat message visible to all connected players via <c>game.print()</c>.
    /// The message is tagged with "[AI]" to distinguish it from player messages.
    /// </summary>
    public Task<string> SendChatMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var escaped = EscapeLuaString(message);
        var lua = $$"""
            game.print("[AI] {{escaped}}")
            rcon.print('{"status":"sent"}')
            """;

        return rcon.ExecuteLuaAsync(lua, cancellationToken);
    }

    /// <summary>
    /// Escape a string for safe embedding in a Lua double-quoted string literal.
    /// </summary>
    private static string EscapeLuaString(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\0': break; // strip null bytes
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
