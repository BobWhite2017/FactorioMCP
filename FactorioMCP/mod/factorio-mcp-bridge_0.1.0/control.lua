local function initialize()
    storage.chat_log = storage.chat_log or {}
    storage.nav_results = storage.nav_results or {}
    storage.pending_paths = storage.pending_paths or {}
end

local function player()
    local p = game.connected_players[1]
    if not p or not p.character or not p.character.valid then
        error("FactorioMCP requires a connected player with a character")
    end
    return p
end

script.on_init(initialize)
script.on_configuration_changed(initialize)

-- Register unconditionally when control.lua loads, including multiplayer joins.
script.on_event(defines.events.on_console_chat, function(e)
    local p = e.player_index and game.get_player(e.player_index)
    local log = storage.chat_log
    log[#log + 1] = {
        tick = e.tick,
        player_name = p and p.name or "server",
        message = e.message
    }
end)

script.on_event(defines.events.on_tick, function()
    local walk = storage.walk
    if walk then
        local p = game.get_player(walk.player_index)
        if p and p.connected and p.character and p.character.valid then
            p.walking_state = {walking = true, direction = walk.direction}
        else
            storage.walk = nil
        end
    end
    local mine = storage.mine
    if mine then
        local p = game.get_player(mine.player_index)
        if p and p.connected and p.character and p.character.valid then
            p.update_selected_entity(mine.position)
            p.mining_state = {mining = true, position = mine.position}
        else
            storage.mine = nil
        end
    end
end)

script.on_event(defines.events.on_script_path_request_finished, function(e)
    if not storage.pending_paths[e.id] then return end
    storage.pending_paths[e.id] = nil
    local status = e.path and #e.path > 0 and "ok"
        or e.try_again_later and "busy" or "no_path"
    storage.nav_results[e.id] = {status = status, path = e.path, tick = game.tick}
end)

remote.add_interface("factorio_mcp_bridge", {
    get_info = function()
        return {status = "ready", bridge_api_version = 1}
    end,
    initialize = function()
        -- A new MCP connection must not resume an abandoned control command.
        storage.walk = nil
        storage.mine = nil
        storage.nav_results = {}
        storage.pending_paths = {}
        return {status = "initialized", bridge_api_version = 1, existing_messages = #storage.chat_log}
    end,
    get_chat_log = function()
        return storage.chat_log
    end,
    set_walking = function(direction)
        local previous = storage.walk
        storage.walk = nil
        if direction ~= nil then
            local p = player()
            storage.walk = {player_index = p.index, direction = direction}
            p.walking_state = {walking = true, direction = direction}
        elseif previous then
            local p = game.get_player(previous.player_index)
            if p and p.valid then
                p.walking_state = {walking = false, direction = defines.direction.north}
            end
        end
    end,
    set_mining = function(position)
        local previous = storage.mine
        storage.mine = nil
        if position then
            local p = player()
            storage.mine = {player_index = p.index, position = position}
        elseif previous then
            local p = game.get_player(previous.player_index)
            if p and p.valid then p.mining_state = {mining = false} end
        end
    end,
    request_path = function(x, y, tolerance)
        local p = player()
        for id, result in pairs(storage.nav_results) do
            if game.tick - result.tick > 600 then storage.nav_results[id] = nil end
        end
        for id, tick in pairs(storage.pending_paths) do
            if game.tick - tick > 600 then storage.pending_paths[id] = nil end
        end
        local id = p.surface.request_path{
            bounding_box = p.character.prototype.collision_box,
            collision_mask = p.character.prototype.collision_mask,
            start = p.position,
            goal = {x = x, y = y},
            force = p.force,
            radius = tolerance,
            entity_to_ignore = p.character,
            pathfind_flags = {prefer_straight_paths = true, no_break = true}
        }
        storage.pending_paths[id] = game.tick
        return id
    end,
    take_path_result = function(id)
        local result = storage.nav_results[id]
        storage.nav_results[id] = nil
        return result
    end
})
