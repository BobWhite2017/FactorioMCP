-- Run from the repository root with Lua 5.2+.
local function read(path)
    local file = assert(io.open(path, "r"))
    local text = file:read("*a")
    file:close()
    return text
end

local common = read("FactorioMCP\\Services\\FactorioService.cs")
local constants = {}
for _, name in ipairs({"LuaJsonEscape", "LuaEntitySort", "LuaInventoryTypes"}) do
    constants[name] = assert(common:match(name .. ' = """(.-)"""'))
    constants["FactorioService." .. name] = constants[name]
end

local function script(path, method, values)
    local source = read(path)
    local start = assert(source:find("Task<string%??>%s+" .. method .. "%("))
    local first = assert(source:find('$$"""', start, true)) + 5
    local last = assert(source:find('"""', first, true)) - 1
    local body = source:sub(first, last)
    for name, value in pairs(constants) do
        body = body:gsub("{{" .. name:gsub("%.", "%%.") .. "}}", function() return value end)
    end
    for name, value in pairs(values) do
        body = body:gsub("{{" .. name .. "}}", function() return tostring(value) end)
    end
    assert(not body:find("{{", 1, true), "unhandled C# interpolation")
    return body
end

local function fixture(can_reach)
    local function inventory(name, count)
        local stack = {name=name, count=count, valid_for_read=count > 0, prototype={stack_size=100}}
        local inv = {stack}
        inv.get_contents = function() return {{name=stack.name, count=stack.count}} end
        inv.get_item_count = function(item) return item == stack.name and stack.count or 0 end
        inv.insert = function(item)
            assert(item.name == stack.name)
            stack.count = stack.count + item.count
            return item.count
        end
        inv.remove = function(item)
            local removed = math.min(stack.count, item.count)
            stack.count = stack.count - removed
            return removed
        end
        inv.count_empty_stacks = function() return stack.count == 0 and 1 or 0 end
        return inv
    end
    local inventories = {
        [1]=inventory("wood", 1), [2]=inventory("iron-ore", 2), [3]=inventory("iron-plate", 1)
    }
    local entity = {
        name="stone-furnace", type="furnace", position={x=-19, y=-5},
        status=1, direction=0, get_recipe=function() return nil end
    }
    entity.get_inventory = function(id)
        assert(type(id) == "number", "missing Factorio 2.1 inventory enum")
        return inventories[id]
    end
    local items = {["iron-ore"]=4, ["iron-plate"]=0, wood=3}
    local surface = {find_entities_filtered=function() return {entity} end}
    local player = {position={x=-14.21, y=-2.74}, reach_distance=10, character={}, surface=surface}
    player.can_reach_entity = function(e) assert(e == entity) return can_reach end
    player.get_item_count = function(item) return items[item] or 0 end
    player.remove_item = function(item) items[item.name] = items[item.name] - item.count end
    player.insert = function(item)
        items[item.name] = (items[item.name] or 0) + item.count
        return item.count
    end
    local output = {}
    local env = setmetatable({
        game={connected_players={player}},
        defines={
            inventory={fuel=1, chest=1, crafter_input=2, crafter_output=3, crafter_modules=4, crafter_trash=8, lab_input=2},
            direction={north=0}, entity_status={normal=1}
        },
        rcon={print=function(text) output[#output+1] = text end}
    }, {__index=_G})
    return env, output, inventories, items
end

local entity_path = "FactorioMCP\\Services\\FactorioService.Entity.cs"
local function run(path, method, inventory_type, can_reach)
    local values = {
        x=-19, y=-5, itemName="iron-ore", count=2, inventoryType=inventory_type,
        escapedItemName="iron-plate", radius=50, ore="iron-ore", outputItem="iron-plate"
    }
    if method == "RemoveItemsAsync" or method == "WaitForEntityInventoryAsync" then
        values.itemName = "iron-plate"
        values.count = 1
    end
    if inventory_type == "fuel" then values.itemName = "wood" end
    local env, output, inventories, items = fixture(can_reach ~= false)
    assert(load(script(path, method, values), method, "t", env))()
    assert(#output == 1, "exactly one query result required")
    return output[1], inventories, items
end

for _, alias in ipairs({"furnace_source", "assembling_machine_input", "crafter_input", "lab_input"}) do
    local result, inventories, items = run(entity_path, "InsertItemsAsync", alias)
    assert(result:find('"inserted":2', 1, true), result)
    assert(inventories[2].get_item_count("iron-ore") == 4 and items["iron-ore"] == 2)
end
for _, alias in ipairs({"furnace_result", "assembling_machine_output", "crafter_output"}) do
    local result, inventories, items = run(entity_path, "RemoveItemsAsync", alias)
    assert(result:find('"transferred":1', 1, true), result)
    assert(inventories[3].get_item_count("iron-plate") == 0 and items["iron-plate"] == 1)
end
assert(run(entity_path, "InsertItemsAsync", "fuel"):find('"inserted":2', 1, true))
assert(run(entity_path, "GetEntityInventoryAsync", "chest"):find('"name":"wood"', 1, true))
assert(run(entity_path, "InsertItemsAsync", "unknown"):find("invalid_inventory_type", 1, true))
assert(run(entity_path, "InsertItemsAsync", "furnace_source", false):find("out_of_range", 1, true))
assert(run(entity_path, "RemoveItemsAsync", "furnace_result", false):find("out_of_range", 1, true))
assert(run(entity_path, "GetEntityReachAsync", "chest"):find('"can_reach":true', 1, true))
assert(run(entity_path, "GetEntityInventoryAsync", "furnace_result"):find('"name":"iron-plate"', 1, true))
assert(run(entity_path, "TransferAllItemsAsync", "furnace_result"):find('"total_items":1', 1, true))
assert(run(entity_path, "InspectEntityAsync", "chest"):find('"furnace_result":[', 1, true))
assert(run("FactorioMCP\\Services\\FactorioService.Wait.cs", "WaitForEntityInventoryAsync", "furnace_result"):find('"count":1', 1, true))
assert(run("FactorioMCP\\Services\\FactorioService.Factory.cs", "FindMissingInputsAsync", "chest"):find('"success":true', 1, true))
local count = run("FactorioMCP\\Services\\FactorioService.World.cs", "CountItemInWorldAsync", "chest")
assert(count:find('"container_count":1', 1, true), count)
local furnace = run("FactorioMCP\\Tools\\TaskTools.cs", "QueryFurnaceStateAsync", "chest")
assert(furnace:find('"source_count":2', 1, true) and furnace:find('"result_count":1', 1, true), furnace)
print("Inventory Lua passed: legacy/native aliases, legal reach, insert/remove, transfer/read/wait/inspect, world totals and smelting state")
