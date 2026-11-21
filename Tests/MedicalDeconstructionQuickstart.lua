-- Disposable single-player fixture. Use quickstart Humpback, then:
-- cl_lua dofile([[LocalMods/Enhanced Experience/Tests/MedicalDeconstructionQuickstart.lua]])
LuaUserData.RegisterType("Barotrauma.GameMain")
LuaUserData.RegisterType("Barotrauma.DebugConsole")
LuaUserData.RegisterType("Barotrauma.Items.Components.Deconstructor")
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
local console = LuaUserData.CreateStatic("Barotrauma.DebugConsole")
local session = main.GameSession
assert(not main.Client and session and session.GameMode.Preset.Identifier.Value == "devsandbox",
    "Use a disposable single-player quickstart")

local results = {}
local resultPath = "LocalMods/Enhanced Experience/Tests/MedicalDeconstructionQuickstart-results.txt"
local function record(name, success, detail)
    local line = name .. ": " .. (success == nil and "RUNNING" or success and "PASS" or "FAIL") ..
        (detail and " (" .. detail .. ")" or "")
    results[#results + 1] = line
    print("[Medical deconstruction] " .. line)
    File.Write(resultPath, table.concat(results, "\n") .. "\n")
end

local function guarded(action)
    return function(...)
        if main.GameSession ~= session then return end
        local ok, message = pcall(action, ...)
        if not ok then record("Fixture", false, tostring(message)) end
    end
end

local function waitFor(seconds, predicate, next)
    if predicate() then next(true) return end
    if seconds <= 0 then next(false) return end
    Timer.Wait(guarded(function() waitFor(seconds - 1, predicate, next) end), 1000)
end

local function spawn(id, destination, onSpawn)
    Entity.Spawner.AddItemToSpawnQueue(assert(ItemPrefab.Prefabs[Identifier(id)], id), destination,
        nil, 0, guarded(onSpawn))
end

local ingredients = {
    "aquaticpoppy", "yeastshroom", "slimebacteria", "adrenalinegland", "swimbladder",
    "smallmudraptoregg", "mucusball", "hallucinogenicbufotoxin", "oxygeniteshard",
    "smallcrawleregg", "sulphuriteshard", "divingknifeunique"
}

local function start(attempts)
    if console.IsOpen then console.Toggle() end
    local doctor, other, device
    for bot in Character.CharacterList do
        if bot.IsBot and bot.IsOnPlayerTeam and not bot.IsDead and bot ~= Character.Controlled and bot.CurrentHull then
            if bot.Info.Job.Prefab.Identifier.Value == "medicaldoctor" then doctor = bot else other = bot end
        end
    end
    if doctor then
        for item in Item.ItemList do
            if item.Prefab.Identifier.Value == "deconstructor" and item.Submarine == doctor.Submarine and
                item.CurrentHull and item.HasAccess(doctor) then
                local machine = item.GetComponentString("Deconstructor")
                if machine and machine.HasPower and not machine.IsActive and machine.InputContainer.Inventory.IsEmpty() then
                    device = machine
                    break
                end
            end
        end
    end
    if not (doctor and other and device and Character.Controlled) then
        if attempts == 0 then record("Fixture", false, "Powered idle deconstructor and crew required") return end
        Timer.Wait(guarded(function() start(attempts - 1) end), 1000)
        return
    end

    local prefab = assert(OrderPrefab.Prefabs[Identifier("deconstructitems")])
    local command = prefab.CreateInstance(OrderPrefab.OrderTargetType.Entity, Character.Controlled, false)
        .WithManualPriority(3)
    local manager = doctor.AIController.ObjectiveManager
    local function hasOrder()
        for entry in manager.CurrentOrders do
            if entry.Identifier.Value == "deconstructitems" then return true end
        end
        return false
    end
    local typeName = "EnhancedExperience.MedicalDeconstructionObjective"
    LuaUserData.RegisterType(typeName)
    record("Doctor receives medical deconstruction", LuaUserData.IsTargetType(manager.CreateObjective(command, 1), typeName))
    record("Other job receives vanilla deconstruction", not LuaUserData.IsTargetType(
        other.AIController.ObjectiveManager.CreateObjective(command, 1), typeName))

    local speed = device.DeconstructionSpeed
    local minVoltage = device.MinVoltage
    local input = device.InputContainer.Inventory
    local output = device.OutputContainer.Inventory
    device.DeconstructionSpeed = 15

    local items, protected, private, testCrate
    items, protected = {}, {}
    local baseline = {}
    for item in Item.ItemList do baseline[item.ID] = true end
    local productIds = {}
    for _, id in ipairs({"opium", "ethanol", "antibiotics", "adrenaline", "stabilozine", "antibloodloss1",
        "alienblood", "antibleeding1", "paralyxis", "calcium", "liquidoxygenite", "sulphuricacid", "morbusine", "iron"}) do
        productIds[id] = true
    end
    local spawned = 0
    local function track(list, id)
        return function(item) list[id] = item spawned = spawned + 1 end
    end
    spawn("metalcrate", device.Item.WorldPosition + Vector2(35, 0), function(crate)
        testCrate = crate
        for _, id in ipairs(ingredients) do
            local source = id == "aquaticpoppy" and device.Item.WorldPosition + Vector2(-40, 10) or crate.OwnInventory
            spawn(id, source, track(items, id))
        end
        for _, id in ipairs({"antidama1", "antidama2", "antibloodloss2", "morbusine", "radiotoxin"}) do
            spawn(id, crate.OwnInventory, track(protected, id))
        end
    end)
    spawn("yeastshroom", Character.Controlled.Inventory, function(item) private = item spawned = spawned + 1 end)
    spawn("antidama1", input, track(protected, "manual"))

    local products = {}
    local function clearFixtureOutputs()
        local snapshot = {}
        for item in Item.ItemList do
            local id = item.Prefab.Identifier.Value
            if productIds[id] and not baseline[item.ID] and item ~= protected.morbusine then
                baseline[item.ID] = true
                products[id] = (products[id] or 0) + 1
                snapshot[#snapshot + 1] = item
            end
        end
        for _, item in ipairs(snapshot) do Entity.Spawner.AddItemToRemoveQueue(item) end
    end

    local function finish()
        doctor.SetOrder(command.GetDismissal(), true, false, true)
        device.DeconstructionSpeed = speed
        device.MinVoltage = minVoltage
        record("Fixture complete", true)
    end

    waitFor(10, function() return spawned == 19 end, function(ready)
        assert(ready, "Could not place all fixture items")
        doctor.SetOrder(command, true, false, true)
        doctor.SetOrder(command, true, false, true)
        local orderCount = 0
        for entry in manager.CurrentOrders do
            if entry.Identifier.Value == "deconstructitems" then orderCount = orderCount + 1 end
        end
        record("Repeated command leaves one objective", orderCount == 1)

        Timer.Wait(guarded(function()
            local occupied = protected.manual
            record("Occupied input is not started automatically", not device.IsActive and not occupied.Removed and input.Contains(occupied))
            input.RemoveItem(occupied)
            local privateInventory = Character.Controlled.Inventory
            assert(privateInventory.TryPutItem(occupied, Character.Controlled, {InvSlotType.Any}, true),
                "Could not move blocked item to the fixture controller")
            device.MinVoltage = math.max(minVoltage, device.Voltage) + 100
            Timer.Wait(guarded(function()
                record("Unpowered deconstructor is skipped and order remains active",
                    not device.IsActive and input.IsEmpty() and hasOrder())
                device.MinVoltage = minVoltage

                local function allProcessed()
                    clearFixtureOutputs()
                    for _, id in ipairs(ingredients) do
                        if not items[id].Removed then return false end
                    end
                    return true
                end
                waitFor(210, allProcessed, function()
                    for _, id in ipairs(ingredients) do
                        local item = items[id]
                        local details
                        if not item.Removed then
                            details = "condition=" .. tostring(item.Condition) .. ", owner=" ..
                                tostring(item.ParentInventory and item.ParentInventory.Owner.Name) ..
                                ", power=" .. tostring(device.HasPower) .. ", output full=" ..
                                tostring(output.IsFull(true))
                        end
                        record("Automatic " .. id, item.Removed, details)
                    end
                    local allProtected = true
                    for id, item in pairs(protected) do
                        if item.Removed and id ~= "manual" then allProtected = false end
                    end
                    record("Finished medicines and poisons preserved", allProtected)
                    record("Foreign inventory untouched", not private.Removed and private.IsOwnedBy(Character.Controlled))
                    record("Native deconstructor produced output", (products.opium or 0) > 0,
                        "new opium observed=" .. tostring(products.opium or 0))

                    waitFor(10, function() return not hasOrder() end, function(dismissed)
                        record("Order dismisses itself when no ingredients remain", dismissed)
                        spawn("aquaticpoppy", device.Item.WorldPosition + Vector2(-30, 10), function(newItem)
                            doctor.SetOrder(command, true, false, true)
                            waitFor(40, function() return newItem.Removed end, function(refilled)
                                record("Ingredient added later is processed", refilled)
                                spawn("antidama1", device.Item.WorldPosition + Vector2(25, 10), function(manual)
                                    local mark = OrderPrefab.Prefabs[Identifier("deconstructthis")]
                                        .CreateInstance(OrderPrefab.OrderTargetType.Entity, Character.Controlled, false)
                                        .WithTargetEntity(manual)
                                    assert(session.CrewManager.AddOrder(mark, nil), "Standard designation did not register")
                                    doctor.SetOrder(command, true, false, true)
                                    waitFor(45, function() return manual.Removed end, function(processed)
                                        record("Player-marked non-whitelisted item is processed", processed)
                                        doctor.SetOrder(command.GetDismissal(), true, false, true)
                                        for entry in manager.CurrentOrders do
                                            assert(entry.Identifier.Value ~= "deconstructitems", "Dismissal left the order active")
                                        end
                                        spawn("aquaticpoppy", testCrate.OwnInventory, function(afterDismissal)
                                            Timer.Wait(guarded(function()
                                                record("Dismissal stops automatic collection",
                                                    not afterDismissal.Removed and testCrate.OwnInventory.Contains(afterDismissal),
                                                    "removed=" .. tostring(afterDismissal.Removed) .. ", owner=" ..
                                                    tostring(afterDismissal.ParentInventory and afterDismissal.ParentInventory.Owner.Name))
                                                finish()
                                            end), 7000)
                                        end)
                                    end)
                                end)
                            end)
                        end)
                    end)
                end)
            end), 6500)
        end), 5500)
    end)
end

record("Fixture", nil)
Timer.Wait(guarded(function() start(35) end), 1000)
