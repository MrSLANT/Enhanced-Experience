-- Run in a fresh, disposable single-player quickstart:
-- cl_lua dofile([[LocalMods/Enhanced Experience/Tests/MedicalCleanupQuickstart.lua]])
LuaUserData.RegisterType("Barotrauma.GameMain")
LuaUserData.RegisterType("Barotrauma.DebugConsole")
local cleanupType = LuaUserData.RegisterType("EnhancedExperience.MedicalCleanupObjective")
LuaUserData.MakeMethodAccessible(cleanupType, "DescribeRemainingWork")
LuaUserData.MakeMethodAccessible(cleanupType, "GetRemainingWorkForTest")
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
local console = LuaUserData.CreateStatic("Barotrauma.DebugConsole")
local session = main.GameSession
assert(main.Client == nil and session and session.GameMode.Preset.Identifier.Value == "devsandbox",
    "Run in a single-player quickstart")

local logPath = "LocalMods/Enhanced Experience/Tests/MedicalCleanupQuickstart-results.txt"
local results = {}
local writeResult = EnhancedExperienceQuickstartWriteResult or File.Write
local function record(name, status, detail)
    local line = name .. ": " .. status .. (detail and " (" .. detail .. ")" or "")
    results[#results + 1] = line
    print("[Medical cleanup test] " .. line)
    writeResult(logPath, table.concat(results, "\n") .. "\n")
end

local function guarded(action)
    return function(...)
        if main.GameSession ~= session then return end
        local ok, reason = pcall(action, ...)
        if not ok then record("Fixture", "FAIL", tostring(reason)) end
    end
end

local function spawn(id, destination, onSpawn)
    Entity.Spawner.AddItemToSpawnQueue(assert(ItemPrefab.Prefabs[Identifier(id)], id),
        destination, nil, 0, guarded(onSpawn))
end

local function untilCondition(seconds, predicate, done)
    if predicate() then done(true) return end
    if seconds <= 0 then done(false) return end
    Timer.Wait(guarded(function() untilCondition(seconds - 1, predicate, done) end), 1000)
end

local function occupied(inventory)
    local slots = {}
    local all = 0
    for i = 0, inventory.Capacity - 1 do
        local first = inventory.GetItemAt(i)
        if first then
            local count = 0
            for _ in inventory.GetItemsAt(i) do count = count + 1 end
            slots[#slots + 1] = {name = tostring(first.Name), count = count}
            all = all + count
        end
    end
    return slots, all
end

local function isSorted(inventory)
    local slots = occupied(inventory)
    for i = 2, #slots do
        if slots[i - 1].name > slots[i].name then return false end
    end
    return true
end

local replacementName = "EnhancedExperience.MedicalCleanupObjective"
local function run(attempts)
    if console.IsOpen then console.Toggle() end
    local doctor, other
    for bot in Character.CharacterList do
        if bot.IsBot and bot.IsOnPlayerTeam and not bot.IsDead and bot ~= Character.Controlled then
            if bot.Info.Job.Prefab.Identifier.Value == "medicaldoctor" then doctor = bot
            else other = bot end
        end
    end

    if not doctor or not doctor.CurrentHull or not other then
        if attempts == 0 then record("Fixture", "FAIL", "Crew unavailable") return end
        Timer.Wait(guarded(function() run(attempts - 1) end), 1000)
        return
    end

    local orderPrefab = assert(OrderPrefab.Prefabs[Identifier("cleanupitems")])
    local cleanup = orderPrefab.CreateInstance(OrderPrefab.OrderTargetType.Entity, Character.Controlled, false)
        .WithManualPriority(3)
    local medicManager = doctor.AIController.ObjectiveManager
    local otherManager = other.AIController.ObjectiveManager
    local doctorObjective = medicManager.CreateObjective(cleanup, 1)
    local otherObjective = otherManager.CreateObjective(cleanup, 1)
    record("Medical Doctor factory", LuaUserData.IsTargetType(doctorObjective, replacementName) and "PASS" or "FAIL")
    record("Other profession factory", not LuaUserData.IsTargetType(otherObjective, replacementName) and "PASS" or "FAIL")

    local setup = {}
    local fixtureReady = 0
    local function counted(name)
        return function(item)
            setup[name] = item
            fixtureReady = fixtureReady + 1
        end
    end

    local function findCabinet(id)
        local best, distance
        for item in Item.ItemList do
            if item.Prefab.Identifier.Value == id and item.Submarine == doctor.Submarine and
                item.CurrentHull and item.OwnInventory and item.HasAccess(doctor) then
                local offset = item.WorldPosition - doctor.WorldPosition
                local squared = offset.X * offset.X + offset.Y * offset.Y
                if not distance or squared < distance then best, distance = item, squared end
            end
        end
        return assert(best, "No accessible " .. id .. " on this submarine")
    end

    setup.medcabinet = findCabinet("medcabinet")
    setup.toxcabinet = findCabinet("toxcabinet")

    local function seed(cabinet, name, id, slot, poison)
        local inv = cabinet.OwnInventory
        spawn(id, inv, function(item)
            if poison then item.AddTag(Identifier("poison")) end
            assert(inv.TryPutItem(item, slot, false, false, nil), "Could not prepare separate stack " .. name)
            counted(name)(item)
        end)
    end
    local function freeSlots(cabinet)
        local inv = cabinet.OwnInventory
        local slots = {}
        for index = 0, inv.Capacity - 1 do
            local i = inv.Capacity - 1 - index
            if inv.GetItemAt(i) == nil and i < 16 then slots[#slots + 1] = i end
        end
        assert(#slots >= 3, "Fixture cabinet has fewer than three available main slots")
        return slots
    end
    local medicineSlots = freeSlots(setup.medcabinet)
    local toxinSlots = freeSlots(setup.toxcabinet)
    seed(setup.medcabinet, "m1", "antidama1", medicineSlots[1], false)
    seed(setup.medcabinet, "m2", "antidama1", medicineSlots[2], false)
    seed(setup.medcabinet, "m3", "antibleeding1", medicineSlots[3], false)
    seed(setup.toxcabinet, "t1", "radiotoxin", toxinSlots[1], false)
    seed(setup.toxcabinet, "t2", "radiotoxin", toxinSlots[2], false)
    seed(setup.toxcabinet, "t3", "antidama1", toxinSlots[3], true)
    spawn("radiotoxin", setup.toxcabinet.WorldPosition + Vector2(15, 0), counted("radiotoxin"))
    spawn("antidama1", setup.medcabinet.WorldPosition + Vector2(-15, 0), counted("morphine"))
    spawn("antidama1", setup.toxcabinet.WorldPosition + Vector2(-25, 0), function(item)
        item.AddTag(Identifier("poison"))
        counted("mixed")(item)
    end)

    spawn("radiotoxin", setup.medcabinet.OwnInventory, counted("misfiledToxin"))
    spawn("antidama1", setup.toxcabinet.OwnInventory, counted("misfiledMedicine"))
    spawn("metalcrate", setup.medcabinet.WorldPosition + Vector2(55, 0), function(crate)
        spawn("antibleeding1", assert(crate.OwnInventory), counted("crateMedicine"))
    end)
    untilCondition(10, function() return fixtureReady == 12 end, function(ok)
        assert(ok, "Could not install all fixtures")
        doctor.SetOrder(cleanup, true, false, true)
        doctor.SetOrder(cleanup, true, false, true)
        local orders = 0
        for entry in medicManager.CurrentOrders do
            if entry.Identifier.Value == "cleanupitems" then orders = orders + 1 end
        end
        record("Repeated order leaves one cleanup objective", orders == 1 and "PASS" or "FAIL")

        local function hasDestination(item, fixtureCabinet)
            local actual = item.Container
            if not actual then return false end
            local id = fixtureCabinet.Prefab.Identifier.Value
            local tag = id == "toxcabinet" and "toxcab" or "medcab"
            return actual.Prefab.Identifier.Value == id or actual.HasTag(Identifier(tag))
        end
        local function correctDestinations()
            return hasDestination(setup.radiotoxin, setup.toxcabinet) and
                hasDestination(setup.morphine, setup.medcabinet) and
                hasDestination(setup.mixed, setup.toxcabinet) and
                hasDestination(setup.misfiledToxin, setup.toxcabinet) and
                hasDestination(setup.misfiledMedicine, setup.medcabinet) and hasDestination(setup.crateMedicine, setup.medcabinet)
        end
        untilCondition(65, correctDestinations, function()
            Timer.Wait(guarded(function()
                for _, pair in ipairs({
                    {"Radiotoxin to Toxin Cabinet", setup.radiotoxin, setup.toxcabinet},
                    {"Morphine to Medicine Cabinet", setup.morphine, setup.medcabinet},
                    {"Poison+medical takes toxin route", setup.mixed, setup.toxcabinet},
                    {"Misfiled poison moved between cabinets", setup.misfiledToxin, setup.toxcabinet},
                    {"Misfiled medicine moved between cabinets", setup.misfiledMedicine, setup.medcabinet},
                    {"Medicine collected from a loose crate", setup.crateMedicine, setup.medcabinet}
                }) do
                    record(pair[1], hasDestination(pair[2], pair[3]) and "PASS" or "FAIL",
                        pair[2].Container and pair[2].Container.Prefab.Identifier.Value or "still outside cabinet")
                end
                for _, cabinet in ipairs({setup.medcabinet, setup.toxcabinet}) do
                    local _, count = occupied(cabinet.OwnInventory)
                    local isMedicine = cabinet == setup.medcabinet
                    local first = isMedicine and setup.m1 or setup.t1
                    local second = isMedicine and setup.m2 or setup.t2
                    local delivered = isMedicine and setup.morphine or setup.radiotoxin
                    local inv = cabinet.OwnInventory
                    local index = inv.FindIndex(first)
                    local merged = index >= 0 and inv.FindIndex(second) == index and inv.FindIndex(delivered) == index
                    record("Separate and new items merged in " .. cabinet.Prefab.Identifier.Value,
                        merged and "PASS" or "FAIL", "items=" .. count .. ", slots=" .. index ..
                            "/" .. inv.FindIndex(second) .. "/" .. inv.FindIndex(delivered))
                    record("Alphabetical order in " .. cabinet.Prefab.Identifier.Value,
                        isSorted(inv) and "PASS" or "FAIL")
                end

                local wait = OrderPrefab.Prefabs[Identifier("wait")]
                    .CreateInstance(OrderPrefab.OrderTargetType.Entity, Character.Controlled, false)
                    .WithManualPriority(3)
                for bot in Character.CharacterList do
                    if bot ~= doctor and bot ~= Character.Controlled and bot.IsBot and
                        bot.IsOnPlayerTeam and not bot.IsDead then
                        bot.SetOrder(wait, true, false, true)
                    end
                end

                -- Deny access to all toxin cabinets on the fixture submarine and verify
                -- that the loose toxin remains outside the medicine cabinet.
                local disabled = {}
                for item in Item.ItemList do
                    if item.OwnInventory and
                        (item.Prefab.Identifier.Value == "toxcabinet" or item.HasTag(Identifier("toxcab"))) then
                        disabled[#disabled + 1] = {item, item.NonInteractable, item.OwnInventory.Locked}
                        item.NonInteractable = true
                        item.OwnInventory.Locked = true
                    end
                end
                local restored = false
                local function restore()
                    if restored then return end
                    restored = true
                    for _, entry in ipairs(disabled) do
                        if not entry[1].Removed then
                            entry[1].NonInteractable = entry[2]
                            entry[1].OwnInventory.Locked = entry[3]
                        end
                    end
                end
                Timer.Wait(guarded(restore), 8000)
                spawn("radiotoxin", doctor.WorldPosition, function(blocked)
                    doctor.SetOrder(cleanup, true, false, true)
                    Timer.Wait(guarded(function()
                        local owner = blocked.GetRootInventoryOwner()
                        local container = blocked.Container
                        local untouched = blocked.ParentInventory == nil
                        local detail = container and container.Prefab.Identifier.Value or
                            (not untouched and "owner=" .. tostring(owner and owner.Name) or "still loose")
                        record("Unavailable toxin cabinets leave poison alone", untouched and "PASS" or "FAIL", detail)
                        restore()
                        untilCondition(45, function()
                            return blocked.Container and blocked.Container.Prefab.Identifier.Value == "toxcabinet"
                        end, function(retried)
                            record("Poison transferred after cabinet access restored", retried and "PASS" or "FAIL")
                            -- Remove unrelated pending items only in this disposable round,
                            -- after checking the final toxin transfer.
                            if retried then
                                local removed = 0
                                for entry in medicManager.CurrentOrders do
                                    if entry.Identifier.Value == "cleanupitems" then
                                        for item in entry.Objective.GetRemainingWorkForTest() do
                                            if item ~= blocked then
                                                Entity.Spawner.AddItemToRemoveQueue(item)
                                                removed = removed + 1
                                            end
                                        end
                                    end
                                end
                                record("Background cleanup targets queued for removal", "INFO", tostring(removed))
                            end
                            untilCondition(25, function()
                                for entry in doctor.AIController.ObjectiveManager.CurrentOrders do
                                    if entry.Identifier.Value == "cleanupitems" then return false end
                                end
                                return true
                            end, function(dismissed)
                                local current = medicManager.CurrentObjective
                                record("Cleanup order dismisses itself after the last item", dismissed and "PASS" or "FAIL",
                                    not dismissed and ("active objective=" ..
                                        (current and current.Identifier.Value or "none") ..
                                        (current and LuaUserData.IsTargetType(current, replacementName) and
                                            (", " .. current.DescribeRemainingWork()) or "")) or nil)
                                record("Fixture", "COMPLETE")
                            end)
                        end)
                    end), 6000)
                end)
            end), 1500)
        end)
    end)
end

record("Fixture", "RUNNING")
Timer.Wait(guarded(function() run(20) end), 1000)
