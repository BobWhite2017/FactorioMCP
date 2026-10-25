# FactorioMCP

An MCP (Model Context Protocol) server that connects to a running Factorio game via RCON and lets an AI agent play the game realistically — walking, crafting, building — without cheating.

```
LLM (Claude/GPT/etc) ←→ [MCP Server (C#, stdio)] ←→ [RCON TCP] ←→ Factorio Game
```

## Key Principle

The AI player **does not cheat**. It walks to locations using real game physics, waits for crafting to finish, and only interacts with entities within range. No teleportation, no spawning items, no skipping time.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build; Windows x64 .NET 10 runtime for the framework-dependent Windows publication below
- [Factorio](https://factorio.com/) (any edition that supports RCON)
- The included `factorio-mcp-bridge` 0.1.0 mod (Factorio 2.1, bridge API 1), enabled on the server and every graphical client

## Setup

### 1. Launch Factorio with RCON

First copy the complete `FactorioMCP\mod\factorio-mcp-bridge_0.1.0` folder into
both isolated Factorio mod directories and enable `factorio-mcp-bridge` in
each `mod-list.json`. The directory containing `info.json` must be named
`factorio-mcp-bridge_0.1.0`, directly inside each mods directory. Publication
also includes this folder under `server\mod`; this is a distribution artifact,
not an automatically activated Factorio mod directory. Use matching mod
versions on the server and clients; restart both after installing.

The bridge persistently registers chat, tick and path-completion handlers in
its `control.lua`, so they are recreated on map download, reconnect and reload.
RCON still performs ordinary operations; only event-owned state passes through
the bridge's small remote interface. Startup rejects a missing or incompatible
bridge explicitly, without injecting scenario event handlers as a fallback.
Movement and mining remain tick-based and consume normal player resources.
The tick handler remembers the selected player's index and stops controlling
them when disconnected rather than switching to another connected player.

If an earlier server version already injected handlers into a test save's
`__level__` scenario, installing the bridge does not remove those old
registrations. Preserve that test save and start from a clean separate test
save; do not blanket-remove scenario handlers or modify primary saves.

For the tested Windows Steam Factorio 2.1.23 setup, run a localhost-only
dedicated server with a **new, separate test save**, then join it using the
graphical client. Ordinary singleplayer and graphical `--host` did not open
RCON in this setup, even though the world loaded.

**Dedicated server** (replace the save path and private password):
```sh
factorio.exe --start-server "path\to\test-save.zip" --bind 127.0.0.1:34197 --rcon-bind 127.0.0.1:27015 --rcon-password "<private-password>"
```

Do not combine `--rcon-bind` with `--rcon-port`, or `--bind` with `--port`;
the bind arguments above already include their ports. The installed executable
explicitly rejects the RCON flag combination.

**Graphical client**, in a separate process with its own configuration and
write-data directory:
```sh
factorio.exe --config "path\to\isolated-client-config.ini" --mp-connect 127.0.0.1:34197
```

Keep server and client write-data separate to avoid lock conflicts and protect
existing saves/settings. Optional client settings include
`--fullscreen=false --window-size 1280x720` for windowed mode and
`master-volume=0.2` in the isolated client configuration for lower volume.
Apply preferences only to the isolated configuration, not normal Factorio settings.
Start the server, then the client, then the MCP connection.

### 2. Configure Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `FACTORIO_RCON_HOST` | `127.0.0.1` | RCON server host |
| `FACTORIO_RCON_PORT` | `27015` | RCON server port |
| `FACTORIO_RCON_PASSWORD` | `mypassword` | RCON password |
| `FACTORIO_GOALS_FILE` | `goals.json` | File path for goal planner persistence |
| `FACTORIO_BUILDINGS_FILE` | `buildings.json` | File path for building memory persistence |

### 3. Build

```sh
dotnet build
```

## Connecting an MCP Client

### Windows local installation (GitHub Copilot desktop app)

Publish a Release build to a stable location outside the worktree:

```powershell
dotnet restore .\FactorioMCP.sln --source https://api.nuget.org/v3/index.json
dotnet test .\FactorioMCP.sln --configuration Release --no-restore --filter 'Category!=Integration'
# Continue only if tests pass:
dotnet publish .\FactorioMCP\FactorioMCP.csproj --configuration Release --runtime win-x64 --self-contained false --output "$env:LOCALAPPDATA\FactorioMCP\server" --source https://api.nuget.org/v3/index.json
New-Item -ItemType Directory -Path "$env:LOCALAPPDATA\FactorioMCP\state\test-save" -Force
```

The explicit restore source avoids broken machine-local NuGet feeds without
changing global settings. Stop any running MCP server before republishing.
The published executable requires the Windows x64 .NET 10 runtime, not the SDK
or .NET 9.

In the desktop app's stdio MCP configuration, use the expanded absolute path to
`%LOCALAPPDATA%\FactorioMCP\server\FactorioMCP.exe` as the command, with no
arguments. Set the following environment variables; expand `%LOCALAPPDATA%`
yourself, since clients need not expand it:

| Variable | Value |
|----------|-------|
| `FACTORIO_RCON_HOST` | `127.0.0.1` |
| `FACTORIO_RCON_PORT` | The port configured for the test game's RCON server |
| `FACTORIO_RCON_PASSWORD` | The matching private RCON password |
| `FACTORIO_GOALS_FILE` | `%LOCALAPPDATA%\FactorioMCP\state\test-save\goals.json` |
| `FACTORIO_BUILDINGS_FILE` | `%LOCALAPPDATA%\FactorioMCP\state\test-save\buildings.json` |

Use a **new, separate test save**, never an existing factory. Keep RCON local
and private. Start the RCON-enabled dedicated server first, connect your player
with the isolated graphical client,
then enable the MCP server. Startup connects to RCON and initializes the chat
listener; it is not an offline service. Player-facing tools target
`game.connected_players[1]`: the AI controls your first connected player while
you watch, not a separate bot. Disconnecting or changing player order can
change the target.

Only one local MCP controller may own an RCON endpoint at a time. Startup
acquires an OS-held exclusive file lease under
`%LOCALAPPDATA%\FactorioMCP\locks` **before connecting or initializing the
bridge**; a second instance exits with an explicit diagnostic instead of
resetting another client's controls. Startup logs include the owning process
ID. Loopback aliases (`localhost`, `127.0.0.1`, and IPv6 loopback) share ownership; different
ports remain independent. The OS releases the lease when the process exits,
so leftover lock files must not be deleted to force ownership.

This is a same-machine, same-user safeguard, not distributed arbitration or a
lock against older MCP builds or raw RCON clients. When upgrading, unload all
old MCP instances first, then enable only the intended controlling client.
Multiple independently controlling clients for the same player are not
supported. No Factorio restart is needed for this server-only safeguard.

Missing state files represent empty memory and are created on first use.
Create the state directory first; do not copy upstream goals or buildings.
Use different state paths for each save to prevent remembered factories and
goals from leaking between saves. Application logs go to stderr; stdout is
reserved for MCP. Prefer the published executable over `dotnet run`, which
can add build output to stdout.

The local Factorio 2.1.23 dedicated-server setup has verified fresh-world
startup, graphical client join with MCP already initialized, discovery of 116
tools, player/inventory reads, bounded walking with a confirmed stop, mining
one natural iron ore into inventory with a confirmed stop, and chat send/read
responses. Receiving a player chat event has not yet been demonstrated.
Live furnace checks also verified waiting for two iron plates via the
`furnace_result` alias, removing both plates, and queuing/completing one iron
gear craft. A composite `Smelt` with one iron ore and
`fuelCount=0` returned `success:true`, `status:"complete"`, `ore_inserted:1`,
`fuel_inserted:0`, `output_collected:1`, and `walk_status:"not_needed"`,
using existing furnace fuel/energy without unnecessary walking. These checks
cover basic harness operations, not a full gameplay progression.

Live disconnect/rejoin and save/restart/rejoin checks are **not yet verified**.
These checks must work without
`multiplayer.script-event-mismatch`; the Lua lifecycle harness
`FactorioMCP.Tests\Lua\BridgeLifecycleTests.lua` covers registration/state
restoration with a simulated loader but does not replace this live check.
RCON Lua commands may disable achievements for that save. `ExecuteLua`
permits arbitrary game changes, so the
no-cheating principle is not a security boundary.

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "factorio": {
      "command": "dotnet",
      "args": ["run", "--project", "path/to/FactorioMCP"],
      "env": {
        "FACTORIO_RCON_PASSWORD": "mypassword"
      }
    }
  }
}
```

### VS Code (GitHub Copilot)

A pre-configured [`.vscode/mcp.json`](.vscode/mcp.json) is included in the repository. Open the workspace in VS Code and it will be detected automatically. Adjust the environment variables if needed.

<details>
<summary>Manual setup</summary>

Add to your `.vscode/mcp.json`:

```json
{
  "servers": {
    "factorio": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "${workspaceFolder}/FactorioMCP"],
      "env": {
        "FACTORIO_RCON_HOST": "127.0.0.1",
        "FACTORIO_RCON_PORT": "27015",
        "FACTORIO_RCON_PASSWORD": "mypassword"
      }
    }
  }
}
```

</details>

### LM Studio (Local Models)

[LM Studio](https://lmstudio.ai/) can connect to MCP servers via its built-in MCP client. This is a good option for running a local model without cloud API costs.

1. **Install LM Studio** and download a model (recommended: `qwen3-32b` or similar with tool-calling support).

2. **Enable MCP** in LM Studio: go to **Settings → MCP** (or the MCP tab in the chat sidebar).

3. **Add the FactorioMCP server** with this configuration:

   ```json
   {
     "factorio": {
       "command": "dotnet",
       "args": ["run", "--project", "path/to/FactorioMCP"],
       "env": {
         "FACTORIO_RCON_HOST": "127.0.0.1",
         "FACTORIO_RCON_PORT": "27015",
         "FACTORIO_RCON_PASSWORD": "mypassword"
       }
     }
   }
   ```

4. **Start a chat** with the model — tools will appear in the MCP tools panel. The model will call tools to interact with Factorio.

> **Note:** Local models may have weaker tool-calling compared to cloud models. If the model struggles to use tools correctly, try a larger parameter model or one specifically fine-tuned for function calling.

## Available Tools

> **Full reference**: See [TOOLS.md](TOOLS.md) for detailed parameters, return values, and AI prompt engineering tips.

### Movement

| Tool | Description |
|------|-------------|
| `WalkForDuration` | Walk in a direction for N seconds, then stop. Returns position after walking. |
| `StopWalking` | Stop the player from walking immediately. |
| `GetPlayerPosition` | Get the player's current map position. |

### Inventory & Crafting

| Tool | Description |
|------|-------------|
| `GetInventory` | List all items and counts in the player's main inventory. |
| `Craft` | Begin crafting items using a recipe. Respects real crafting time. |
| `GetCraftingQueue` | Get the current crafting queue contents. |

### Entity Placement & Mining

| Tool | Description |
|------|-------------|
| `PlaceEntity` | Place an entity from inventory at map coordinates (with direction). Automatically tracked in building memory. |
| `MineEntity` | Mine/remove an entity at map coordinates into inventory. Automatically untracked from building memory. |

### Entity Interaction

| Tool | Description |
|------|-------------|
| `InsertItems` | Insert items from player inventory into a machine (fuel, ore, etc.). |
| `RemoveItems` | Remove items from a machine's inventory into player inventory. |
| `InspectEntity` | Inspect entity status, inventories, fuel, recipe, and health. |

### World Scanning

| Tool | Description |
|------|-------------|
| `GetNearbyEntities` | List all entities near the player within a given radius. |
| `CheckDistance` | Check distance to coordinates and whether within build/reach range. |
| `ScanResources` | Scan for resource patches (ores, oil) with type summaries and center coordinates. |
| `ScanTiles` | Scan terrain tiles for type counts (grass, sand, water, etc.). |

### Wait & Polling

| Tool | Description |
|------|-------------|
| `WaitForCrafting` | Wait for the crafting queue to empty. |
| `WaitForPosition` | Wait until the player reaches target coordinates. |
| `WaitForTicks` | Wait for a specified number of game ticks to elapse. |
| `GetGameTick` | Get the current game tick. |

### Chat

| Tool | Description |
|------|-------------|
| `InitializeChatListener` | Initialize/re-initialize the chat message event listener. |
| `GetChatMessages` | Get chat messages from the in-game chat log (with polling support). |
| `SendChatMessage` | Send an `[AI]`-tagged chat message visible to all players. |

### Goal Planning

| Tool | Description |
|------|-------------|
| `SetGoal` | Create a new goal with optional ordered steps. |
| `GetActiveGoal` | Get the currently active goal with step details. |
| `GetAllGoals` | Get a summary of all goals (completed, failed, suspended, pending). |
| `AdvanceGoalStep` | Mark the current step as completed and advance. |
| `AddGoalSteps` | Add new steps to the active goal. |
| `CompleteGoal` | Mark the active goal as completed. |
| `FailGoal` | Mark the active goal as failed with a reason. |
| `SuspendGoal` | Suspend the active goal to work on something urgent. |
| `ResumeGoal` | Resume a previously suspended goal. |

### Energy

| Tool | Description |
|------|-------------|
| `GetElectricNetwork` | Get electric network stats: production, consumption, satisfaction, accumulators. |
| `InspectEntityPower` | Inspect per-entity power status: connection, energy, buffer, drain. |

### Research

| Tool | Description |
|------|-------------|
| `GetResearchStatus` | Get current research technology and progress percentage. |
| `GetAvailableTechnologies` | List all technologies available for research (prerequisites met). |
| `StartResearch` | Start researching a technology. |

### Recipe & Technology Queries

| Tool | Description |
|------|-------------|
| `GetRecipeDetails` | Get recipe ingredients, products, crafting time, and category. |
| `GetAvailableRecipes` | List all currently unlocked recipes. |
| `GetTechnologyDetails` | Get technology prerequisites, effects, cost, and ingredients. |

### Building Memory

| Tool | Description |
|------|-------------|
| `GetAllBuildings` | Get all tracked buildings with names, positions, directions, and labels. |
| `GetBuildingsNear` | Get tracked buildings near a position within a radius. |
| `FindBuildingsByType` | Find all tracked buildings of a specific entity type. |
| `GetBuildingSummary` | Get counts of each tracked building type. |
| `UpdateBuildingLabel` | Set or remove a label on a tracked building. |
| `ClearBuildingMemory` | Clear all tracked buildings from AI memory (not from game world). |

### Blueprints & Ghosts

| Tool | Description |
|------|-------------|
| `PlaceGhostEntity` | Place a ghost (construction plan) for an entity at map coordinates. |
| `PlaceBlueprintString` | Import and build a blueprint string at a position. |
| `GetGhostEntities` | Scan for ghost entities in an area. |
| `CreateBlueprintFromArea` | Capture an area as an exportable blueprint string. |
| `RevokeGhostEntity` | Remove a ghost entity at a position. |

### Advanced

| Tool | Description |
|------|-------------|
| `ExecuteLua` | Execute arbitrary Lua code via RCON. Use for advanced operations not covered by other tools. |

## Project Structure

```
FactorioMCP/
├── Program.cs                          # Host setup, DI, MCP server wiring
├── Rcon/
│   ├── RconClient.cs                   # Source RCON protocol over TCP
│   └── RconPacket.cs                   # RCON packet types and record
├── Models/
│   ├── Goal.cs                         # Goal/step data model
│   └── TrackedBuilding.cs              # Building memory data model
├── Services/
│   ├── FactorioService.cs              # High-level game operations via Lua
│   ├── EnergyService.cs                # Electric network inspection via Lua
│   ├── BlueprintService.cs             # Blueprint and ghost entity operations
│   ├── GoalPlannerService.cs           # Goal tracking with JSON persistence
│   ├── BuildingMemoryService.cs        # Building tracking with JSON persistence
│   ├── GameCommandQueue.cs             # Serializes concurrent MCP tool calls
│   └── RconConnectionService.cs        # RCON connection on startup
├── Tools/
│   ├── MovementTools.cs                # Walk, stop, get position
│   ├── InventoryTools.cs               # Inventory, crafting
│   ├── EntityTools.cs                  # Place/mine entities
│   ├── InteractionTools.cs             # Insert/remove items, inspect entities
│   ├── WorldTools.cs                   # Nearby entities, distance, scan
│   ├── WaitTools.cs                    # Wait for crafting, position, ticks
│   ├── ChatTools.cs                    # Chat message listener, send/receive
│   ├── GoalTools.cs                    # Goal planning and tracking
│   ├── EnergyTools.cs                  # Electric network and power inspection
│   ├── ResearchTools.cs                # Research status, start research
│   ├── RecipeTools.cs                  # Recipe and technology queries
│   ├── BuildingTools.cs                # Building memory queries
│   ├── BlueprintTools.cs               # Blueprint and ghost entity tools
│   └── LuaTools.cs                     # Raw Lua execution
└── Resources/
    └── GameStateResources.cs           # MCP Resources (read-only game state)

FactorioMCP.Tests/                      # Unit tests (xUnit)
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for workflow guidelines, CPX complexity scale, and coding conventions.

## License

See repository for license details.