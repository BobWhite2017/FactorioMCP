-- Execute the actual generated mining-start Lua against distinct interaction/resource reach.
local file = assert(io.open("FactorioMCP\\Services\\MiningService.cs", "r"))
local source = file:read("*a")
file:close()
local first = assert(source:find('$$"""', 1, true)) + 5
local last = assert(source:find('"""', first, true))
local template = source:sub(first, last - 1)

local function run(entity_x, resource_reach, has_character)
    local calls, output = 0, nil
    local entity = {
        name = "iron-ore", amount = 100, position = {x = entity_x, y = 0},
        prototype = {mineable_properties = {mining_time = 1}}
    }
    local p = {
        position = {x = 0, y = 0}, reach_distance = 10,
        resource_reach_distance = resource_reach,
        character = has_character and {valid = true} or nil,
        surface = {find_entities_filtered = function() return {entity} end},
        update_selected_entity = function() end
    }
    local env = setmetatable({
        game = {connected_players = {p}},
        remote = {call = function(name, method)
            assert(name == "factorio_mcp_bridge" and method == "set_mining")
            calls = calls + 1
        end},
        rcon = {print = function(text) output = text end}
    }, {__index = _G})
    local lua = template:gsub("{{x}}", tostring(entity_x)):gsub("{{y}}", "0")
    assert(load(lua, "mining-start", "t", env))()
    return output, calls, p
end

local output, calls = run(5, 2, true)
assert(output:find('"error":"out_of_range"', 1, true) and calls == 0)
assert(output:find('"limit":2', 1, true))
output, calls = run(1, 2, true)
assert(output:find('"status":"mining_started"', 1, true) and calls == 1)
output, calls = run(1, 2, false)
assert(output:find('"error":"no_character"', 1, true) and calls == 0)
print("Mining Lua passed: resource reach differs from interaction reach, legal start, no-character rejection")
