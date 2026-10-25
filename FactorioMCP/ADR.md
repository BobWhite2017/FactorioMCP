# Architecture Decision Records

Key design decisions for the FactorioMCP project.

---

## ADR-001: RCON Over Mod API

**Decision:** Use RCON + Lua commands for ordinary operations, with a small persistent bridge mod for event-dependent behavior.

**Context:** Factorio supports two extensibility mechanisms:
1. **RCON** — Remote Console protocol over TCP, executes Lua snippets via `/silent-command`
2. **Mods** — Lua scripts loaded into the game, with full event access and `remote.call()` interfaces

**Rationale:**
- **Zero installation** — RCON requires only a command-line flag (`--rcon-port`), no mod files to manage
- **No version coupling** — mods must be updated for each Factorio version; RCON Lua API is more stable
- **Simpler architecture** — direct request/response over TCP vs. async event-driven mod communication
- **Sufficient capability** — all needed operations (movement, crafting, building, scanning) are achievable through RCON Lua execution

**Trade-offs:**
- No event subscriptions (must poll for state changes)
- RCON command execution is synchronous from the caller's perspective
- Some advanced features (pathfinding callbacks, async crafting notifications) would be easier with a mod

**Status:** Amended for Factorio 2.1 lifecycle compatibility. Dynamically
registering `on_console_chat`, `on_tick`, and
`on_script_path_request_finished` through scenario RCON commands broke
multiplayer map loading with `multiplayer.script-event-mismatch`. Lua
functions are not serialized, and joining clients only execute `control.lua`
and `on_load`, so changing MCP startup order cannot fix later rejoins.

The required `factorio-mcp-bridge` mod unconditionally registers these three
handlers in `control.lua`. Its remote API owns chat history, walking/mining
command state and path requests/results in mod-local storage. Other operations
remain direct RCON commands. There is no dynamic scenario-handler fallback.
Both server and clients must enable the matching bridge version, and startup
checks bridge API compatibility explicitly. No storage is written while
`control.lua` loads or during `on_load`; initialization uses `on_init` and
`on_configuration_changed`.

References:
- https://lua-api.factorio.com/latest/auxiliary/data-lifecycle.html
- https://lua-api.factorio.com/latest/auxiliary/storage.html

---

## ADR-002: Realistic AI Constraints

**Decision:** The AI player must not cheat — no teleportation, no item spawning, no instant crafting.

**Context:** The MCP server has full RCON access and could trivially teleport the player, spawn items, or skip crafting time. The question is whether the AI should play like a human or use its privileged access.

**Rationale:**
- **Meaningful gameplay** — cheating removes all challenge and makes the AI uninteresting to watch
- **Transferable strategies** — plans the AI develops can be replicated by human players
- **Emergent complexity** — constraints force the AI to solve logistics, routing, and resource management problems
- **Showcase value** — demonstrating an AI that actually plays the game is more impressive than one that scripts it

**Implementation:**
- Walking uses `player.walking_state` with real physics and stuck detection
- Crafting uses `player.begin_crafting` with real queue times
- Entity interaction validates `build_distance` and `reach_distance` before executing
- Wait/polling tools let the AI wait for crafting completion and arrival at destinations

**Status:** Accepted. This is a core design principle.

---

## ADR-003: Stdio Transport

**Decision:** Use stdio (stdin/stdout) as the MCP transport, not HTTP/SSE.

**Context:** The MCP specification supports multiple transports: stdio, HTTP with Server-Sent Events (SSE), and streamable HTTP. The choice affects how clients connect to the server.

**Rationale:**
- **Universal client support** — all major MCP clients (Claude Desktop, VS Code Copilot, LM Studio) support stdio
- **Simple deployment** — `dotnet run` starts the server; no port management, no HTTP configuration
- **Process isolation** — each client gets its own server process, avoiding shared state issues
- **Security** — no network listener means no attack surface; communication is local-only by design
- **MCP SDK default** — the C# MCP SDK has first-class stdio support via `WithStdioServerTransport()`

**Trade-offs:**
- Only one client per server process (acceptable for single-player Factorio)
- No remote access without wrapping in a network transport
- Logging must go to stderr to avoid corrupting the MCP protocol on stdout

**Status:** Accepted.
