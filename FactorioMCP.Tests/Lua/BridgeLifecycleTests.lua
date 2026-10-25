-- Run with Lua 5.2+ from the repository root:
-- lua FactorioMCP.Tests\Lua\BridgeLifecycleTests.lua FactorioMCP\mod\factorio-mcp-bridge_0.1.0\control.lua
local control = assert(arg[1], "control.lua path required")
local events = {on_console_chat = 88, on_tick = 0, on_script_path_request_finished = 150}

local function copy(value)
    if type(value) ~= "table" then
        assert(type(value) ~= "function", "functions must not be saved")
        return value
    end
    local result = {}
    for k, v in pairs(value) do result[k] = copy(v) end
    return result
end

local function load_bridge(saved)
    local handlers, callbacks, interfaces = {}, {}, {}
    local env = setmetatable({
        defines = {events = events, direction = {north = 0}},
        script = {
            on_init = function(f) callbacks.init = f end,
            on_configuration_changed = function(f) callbacks.changed = f end,
            on_event = function(id, f)
                assert(not handlers[id], "duplicate handler")
                handlers[id] = f
            end
        },
        remote = {add_interface = function(name, methods) interfaces[name] = methods end}
    }, {__index = _G})
    assert(loadfile(control, "t", env))()
    for _, id in pairs(events) do
        assert(type(handlers[id]) == "function", "handler missing on load")
    end
    -- Factorio restores storage only after executing control.lua.
    env.storage = saved and copy(saved) or {}
    local next_id = 0
    local p = {
        index = 1, valid = true, connected = true, name = "observer",
        character = {valid = true, prototype = {collision_box = {}, collision_mask = {}}},
        position = {x = 0, y = 0}, force = {}
    }
    p.surface = {request_path = function(options)
        assert(options.entity_to_ignore == p.character)
        next_id = next_id + 1
        return next_id
    end}
    p.update_selected_entity = function(position) p.selected_position = position end
    env.game = {tick = 100, connected_players = {p}, get_player = function(id)
        return id == 1 and p or nil
    end}
    if not saved then callbacks.init() end
    return env, handlers, interfaces.factorio_mcp_bridge, p
end

local env, handlers, bridge, p = load_bridge()
assert(bridge.get_info().status == "ready" and bridge.get_info().bridge_api_version == 1)
assert(bridge.initialize().bridge_api_version == 1)
handlers[events.on_console_chat]({player_index = 1, tick = 100, message = "hello"})
bridge.set_walking(4)
bridge.set_mining({x = 2, y = 3})
bridge.get_info()
assert(env.storage.walk.direction == 4 and env.storage.mine.position.x == 2,
    "capability probe must not mutate control state")
local id = bridge.request_path(10, 0, 1)

-- Simulate map download/reload: new Lua state, saved tables, no on_init.
local loaded, restored, remote, player = load_bridge(env.storage)
assert(remote.get_chat_log()[1].message == "hello")
restored[events.on_tick]()
assert(player.walking_state.walking and player.walking_state.direction == 4)
assert(player.selected_position.x == 2 and player.mining_state.mining)
restored[events.on_script_path_request_finished]({
    id = id, path = {{position = {x = 10, y = 0}}}
})
assert(remote.take_path_result(id).status == "ok")
assert(remote.take_path_result(id) == nil, "results must be consumed once")
restored[events.on_script_path_request_finished]({id = 999, path = {}})
assert(remote.take_path_result(999) == nil, "ignore other scripts' paths")

local busy_id = remote.request_path(20, 0, 1)
restored[events.on_script_path_request_finished]({id = busy_id, try_again_later = true})
assert(remote.take_path_result(busy_id).status == "busy")
local no_path_id = remote.request_path(20, 0, 1)
restored[events.on_script_path_request_finished]({id = no_path_id})
assert(remote.take_path_result(no_path_id).status == "no_path")

remote.set_walking(nil)
remote.set_mining(nil)
assert(not player.walking_state.walking and not player.mining_state.mining)
remote.set_walking(8)
remote.set_mining({1, 2})
player.connected = false
restored[events.on_tick]()
assert(loaded.storage.walk == nil and loaded.storage.mine == nil)

player.connected = true
remote.set_walking(4)
remote.set_mining({1, 2})
local initialized = remote.initialize()
assert(initialized.existing_messages == 1)
assert(loaded.storage.walk == nil and loaded.storage.mine == nil)
local _, rejoined = load_bridge(loaded.storage)
for id in pairs(restored) do assert(rejoined[id], "registration set changed on rejoin") end
print("Bridge Lua lifecycle: registration, reload, chat, walking, mining, paths and reconnect passed")
