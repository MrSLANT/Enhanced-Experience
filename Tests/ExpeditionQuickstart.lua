-- Run in a disposable single-player quickstart:
-- cl_lua dofile([[LocalMods/Enhanced Experience/Tests/ExpeditionQuickstart.lua]])
LuaUserData.RegisterType("Barotrauma.GameMain")
LuaUserData.RegisterType("Barotrauma.DebugConsole")
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
local console = LuaUserData.CreateStatic("Barotrauma.DebugConsole")
local session = main.GameSession
assert(main.Client == nil and session ~= nil and Submarine.MainSub ~= nil
    and session.GameMode.Preset.Identifier.Value == "devsandbox", "Run in a single-player quickstart")

local mode = ExpeditionTestMode or "crate"
ExpeditionTestMode = nil
assert(mode == "crate" or mode == "floor" or mode == "scenarios" or mode == "depth", "Expected crate, floor, scenarios or depth")
local resultPath = "LocalMods/Enhanced Experience/Tests/ExpeditionQuickstart" ..
    (mode ~= "crate" and ("-" .. mode) or "") .. "-results.txt"
local results = {}
local function record(name, result, detail)
    local line = name .. ": " .. result .. (detail and " (" .. detail .. ")" or "")
    results[#results + 1] = line
    print("[Expedition test] " .. line)
    File.Write(resultPath, table.concat(results, "\n") .. "\n")
end

local function guarded(run)
    return function(...)
        local ok, message = pcall(run, ...)
        if not ok then record("Test harness", "FAIL", tostring(message)) end
    end
end

local bot
local issued
local stock
local fixture = {
    "pucs", "oxygenitetank", "oxygenitetank", "oxygentank", "oxygentank", "oxygentank",
    "plasmacutter", "underwaterscooter", "batterycell",
    "antibleeding2", "antibleeding2", "antibloodloss2", "antidama1"
}
local function spawn(id, destination, callback)
    local prefab = assert(ItemPrefab.Prefabs[Identifier(id)], id)
    Entity.Spawner.AddItemToSpawnQueue(prefab, destination, nil, 0, callback)
end

local function spawnCrate()
    spawn("metalcrate", bot.WorldPosition, guarded(function(crate)
        stock = assert(crate.OwnInventory, "Fixture crate has no inventory")
        for _, id in ipairs(fixture) do
            spawn(id, id == "pucs" and bot.WorldPosition or stock)
        end
    end))
end

local function order(id)
    local prefab = assert(OrderPrefab.Prefabs[Identifier(id)], id)
    return prefab.CreateInstance(OrderPrefab.OrderTargetType.Entity, Character.Controlled, false).WithManualPriority(3)
end

local function objective(id)
    for current in bot.AIController.ObjectiveManager.CurrentOrders do
        if current.Identifier.Value == id then return current.Objective end
    end
end

local function debugTag()
    local active = objective("prepareforexpedition")
    return active and tostring(active.DebugTag) or "missing objective"
end

local function inventory()
    local all = {}
    local function read(container)
        for item in container.AllItems do
            if not all[item] then
                all[item] = true
                if item.OwnInventory then read(item.OwnInventory) end
            end
        end
    end
    read(bot.Inventory)
    local counts, morphine, spare, installed = {}, nil, 0, 0
    local toolCharge = {}
    for item in pairs(all) do
        local id = item.Prefab.Identifier.Value
        counts[id] = (counts[id] or 0) + 1
        if id == "antidama1" then morphine = item end
        if (id == "plasmacutter" or id == "underwaterscooter") and item.OwnInventory then
            local load = {}
            for cell in item.OwnInventory.AllItems do
                load[#load + 1] = cell.Prefab.Identifier.Value .. "=" .. string.format("%.0f%%", cell.ConditionPercentage)
            end
            toolCharge[id] = #load > 0 and table.concat(load, "+") or "empty"
        end
        if id == "oxygenitetank" or id == "oxygentank" then
            if item.Container and (item.Container.HasTag(Identifier("diving")) or item.Container.HasTag(Identifier("tool"))) then
                installed = installed + 1
            elseif item.ConditionPercentage >= 10 then
                spare = spare + 1
            end
        end
    end
    return counts, morphine, spare, installed, toolCharge
end

local function describe()
    local counts, _, spare, installed, toolCharge = inventory()
    local worn = bot.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes)
    local parts = {debugTag(), "bot=" .. bot.Name, "worn=" .. (worn and worn.Prefab.Identifier.Value or "none"),
        "free=" .. bot.Inventory.EmptySlotCount, "spare=" .. spare, "installed=" .. installed,
        "cutterLoad=" .. (toolCharge.plasmacutter or "-"), "scooterLoad=" .. (toolCharge.underwaterscooter or "-")}
    for _, id in ipairs({"pucs", "oxygenitetank", "oxygentank", "plasmacutter", "underwaterscooter",
        "antibleeding2", "antibloodloss2", "antidama1"}) do
        parts[#parts + 1] = id .. "=" .. (counts[id] or 0)
    end
    return table.concat(parts, ", ")
end

local function waitForReady(attempts, done)
    if main.GameSession ~= session then return end
    local ready = debugTag():find("(Ready)", 1, true) ~= nil
    if ready or attempts == 0 then
        done(ready)
    else
        Timer.Wait(guarded(function() waitForReady(attempts - 1, done) end), 1000)
    end
end

local function finish()
    bot.SetOrder(issued.GetDismissal(), true, false, true)
    record("Dismissal removes expedition order", objective("prepareforexpedition") == nil and "PASS" or "FAIL")
    local weapon = order("findweapon")
    local weaponObjective = bot.AIController.ObjectiveManager.CreateObjective(weapon, 1)
    local target = weaponObjective and tostring(weaponObjective.DebugTag) or ""
    record("findweapon factory remains vanilla", target:find("findweapon", 1, true) and "PASS" or "FAIL", target)
end

-- Depth injection is limited to the disposable round. The production pressure and
-- gear checks still execute against the real worn item and its Wearable component.
local function testDepth()
    local level = assert(Level.Loaded)
    local parameters = level.GenerationParams
    local originalHeight = parameters.Height
    local submarine = assert(bot.Submarine)
    local originalCrushDepth = submarine.RealWorldCrushDepth
    local restored = false
    submarine.SetCrushDepth(9000)
    local function setDepth(metres, y)
        local current = level.GetRealWorldDepth(y)
        local metresPerUnit = (level.GetRealWorldDepth(y - 10000) - current) / 10000
        parameters.Height = math.ceil(parameters.Height + (metres - current) / metresPerUnit)
        return level.GetRealWorldDepth(y)
    end
    local function clearDepth()
        if restored then return end
        restored = true
        parameters.Height = originalHeight
        if not submarine.Removed then submarine.SetCrushDepth(originalCrushDepth) end
    end
    Timer.Wait(guarded(clearDepth), 12000)
    local initialDepth = setDepth(6300, bot.WorldPosition.Y)
    assert(math.abs(initialDepth - 6300) < 1, "Fixture could not set 6300 m")

    local function checkFloodGate()
        local objectiveType = "EnhancedExperience.AIObjectivePrepareForExpedition"
        local active = objective("prepareforexpedition")
        local descriptor = LuaUserData.RegisterType(objectiveType)
        LuaUserData.MakeMethodAccessible(descriptor, "AbyssAllowed")
        local hulls = {}
        for hull in Hull.HullList do
            if hull.Submarine == bot.Submarine and not hull.Removed and hull.Volume > 0 then
                hulls[#hulls + 1] = {hull = hull, original = hull.WaterVolume}
            end
        end
        assert(#hulls > 0, "No valid hulls on the fixture submarine")

        local function check(currentDepth, fraction, expected)
            local depth = setDepth(currentDepth, bot.Submarine.WorldPosition.Y)
            local water, volume = 0, 0
            for _, entry in ipairs(hulls) do
                entry.hull.WaterVolume = entry.hull.Volume * fraction
                water = water + entry.hull.WaterVolume
                volume = volume + entry.hull.Volume
            end
            local allowed = active.AbyssAllowed()
            record(string.format("Simulated depth %d m, volume-weighted flooding %.4f%%", depth, water / volume * 100),
                allowed == expected and "PASS" or "FAIL")
        end

        local ok, err = pcall(function()
            check(6499, 0.801, false)
            check(6500, 0.79, false)
            check(6500, 0.801, true)
        end)
        for _, entry in ipairs(hulls) do entry.hull.WaterVolume = entry.original end
        if not ok then error(err) end
    end
    Timer.Wait(guarded(function()
        if main.GameSession ~= session then clearDepth() return end
        local worn = bot.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes)
        local pucs = worn and worn.Prefab.Identifier.Value == "pucs"
        local unsafe = debugTag():find("(PressureUnsafe)", 1, true) ~= nil
        record("Simulated 6300 m: PUCS remains worn but pressure is unsafe",
            pucs and unsafe and bot.CurrentHull ~= nil and "PASS" or "FAIL",
            string.format("depth=%.2f m, %s", level.GetRealWorldDepth(bot.WorldPosition.Y), describe()))

        local floodTest, floodError = pcall(checkFloodGate)
        if not floodTest then record("Live flooding gate", "NOT TESTED", tostring(floodError)) end

        local gearType = "Barotrauma.AIObjectiveFindDivingGear"
        LuaUserData.RegisterType(gearType)
        local divingGear = LuaUserData.CreateStatic(gearType)
        local planned, accepted = pcall(function()
            local pluginType = "EnhancedExperience.ExpeditionPlugin"
            LuaUserData.RegisterType(pluginType)
            return LuaUserData.CreateStatic(pluginType).TrySetPlannedDepth(bot, 6501)
        end)
        record("Abyss plan of 6501 m applied", planned and accepted and "PASS" or "NOT TESTED")
        local targetDepth = setDepth(6800, bot.WorldPosition.Y)
        assert(math.abs(targetDepth - 6800) < 1, "Fixture could not set 6800 m")
        spawn("abyssdivingsuit", bot.WorldPosition, guarded(function(abyss)
            -- Outfit the disposable fixture explicitly after clearing its single outer slot.
            spawn("oxygentank", assert(abyss.OwnInventory), guarded(function(_)
                if worn then Entity.Spawner.AddItemToRemoveQueue(worn) end
                Timer.Wait(guarded(function()
                    local wornNow = bot.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes)
                    local outer = bot.Inventory.FindLimbSlot(InvSlotType.OuterClothes)
                    local equipped = wornNow == abyss or bot.Inventory.TryPutItem(abyss, outer, false, false, bot)
                    Timer.Wait(guarded(function()
                        if main.GameSession ~= session then clearDepth() return end
                        local actual = bot.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes)
                        local pressure = debugTag():find("(PressureUnsafe)", 1, true) ~= nil
                        local stockPressureSafe = divingGear.IsSuitablePressureProtection(abyss, Identifier("deepdiving"), bot)
                        record("Simulated 6800 m: worn Abyss does not confer readiness",
                            equipped and actual == abyss and stockPressureSafe == false and pressure and "PASS" or "FAIL",
                            string.format("depth=%.2f m, %s", level.GetRealWorldDepth(bot.WorldPosition.Y), describe()))
                        clearDepth()
                        finish()
                    end), 2300)
                end), 500)
            end))
        end))
    end), 2300)
end

local function testFire()
    LuaUserData.RegisterType("Barotrauma.FireSource")
    local FireSource = LuaUserData.CreateStatic("Barotrauma.FireSource", true)
    local _, morphine = inventory()
    assert(morphine and bot.CurrentHull, "A ready bot must have morphine and be indoors")
    local fire = FireSource(bot.WorldPosition, bot.CurrentHull, nil, false)
    if not fire.Hull then
        record("Fire emergency", "NOT TESTED", "Fixture did not produce a fire in the hull")
        testDepth()
        return
    end
    fire.DamagesItems = false
    fire.DamagesCharacters = false
    fire.Size = Vector2(90, 35)
    Timer.Wait(guarded(function()
        if main.GameSession == session and not fire.Removed then fire.Remove() end
    end), 5000)

    Entity.Spawner.AddItemToRemoveQueue(morphine)
    local manager = bot.AIController.ObjectiveManager
    local function observe(attempts)
        if main.GameSession ~= session then return end
        local active = manager.CurrentObjective
        local tag = active and tostring(active.DebugTag) or "none"
        local emergency = tag:lower():find("safety", 1, true) ~= nil or tag:lower():find("fire", 1, true) ~= nil
        if not emergency and attempts > 0 and not fire.Removed then
            Timer.Wait(guarded(function() observe(attempts - 1) end), 250)
            return
        end
        if not fire.Removed then fire.Remove() end
        record("Fire emergency takes priority", emergency and "PASS" or "FAIL", tag)
        spawn("antidama1", stock or bot.WorldPosition)
        waitForReady(70, function(resumed)
            record("Completes the missing supply after fire is removed",
                resumed and "PASS" or "FAIL", describe())
            if resumed then testDepth() else finish() end
        end)
    end
    Timer.Wait(guarded(function() observe(15) end), 250)
end

record("Quickstart fixture", "RUNNING")
local function start(attempts)
    if main.GameSession ~= session then return end
    if console.IsOpen then console.Toggle() end
    for candidate in Character.CharacterList do
        if candidate.IsBot and candidate.IsOnPlayerTeam and not candidate.IsDead and candidate ~= Character.Controlled
            and candidate.Submarine == Submarine.MainSub and candidate.CurrentHull ~= nil then
            local worn = candidate.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes)
            if mode == "depth" then
                if worn and worn.Prefab.Identifier.Value == "pucs" then bot = candidate break end
            elseif bot == nil or candidate.Inventory.EmptySlotCount > bot.Inventory.EmptySlotCount then
                bot = candidate
            end
        end
    end
    if not bot then
        if attempts == 0 then record("Test harness", "FAIL", "No friendly bot onboard") return end
        Timer.Wait(guarded(function() start(attempts - 1) end), 1000)
        return
    end
    if mode == "depth" then
        issued = order("prepareforexpedition")
        bot.SetOrder(issued, true, false, true)
        waitForReady(15, function(ready)
            if ready then testDepth() else record("Depth fixture", "NOT TESTED", describe()) finish() end
        end)
        return
    end
    -- Clear only the test bot in this disposable round to give the fixture known capacity.
    local startingItems = {}
    for item in bot.Inventory.AllItems do startingItems[#startingItems + 1] = item end
    for _, item in ipairs(startingItems) do Entity.Spawner.AddItemToRemoveQueue(item) end
    if mode == "floor" then
        for index, id in ipairs(fixture) do
            spawn(id, bot.WorldPosition + Vector2((index % 5 - 2) * 24, (index % 3) * 10))
        end
    else
        spawnCrate()
    end
    Timer.Wait(guarded(function()
        issued = order("prepareforexpedition")
        bot.SetOrder(issued, true, false, true)
        local tag = debugTag()
        local patched = tag:match("^prepareforexpedition %(%a+%)$") ~= nil
        record("Patched order instantiated in game", patched and "PASS" or "FAIL", tag)
        if not patched then finish() return end
        waitForReady(90, function(ready)
            local worn = bot.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes)
            record("PUCS equipped in dry submarine", worn and worn.Prefab.Identifier.Value == "pucs" and "PASS" or "FAIL")
            local _, _, spare = inventory()
            ready = ready and spare == 2
            record("Dry-sub pickup from " .. mode .. ", dressing and complete kit", ready and "PASS" or "FAIL", describe())
            if not ready then
                if mode ~= "floor" then finish() return end
                spawnCrate()
                Timer.Wait(guarded(function()
                    waitForReady(90, function(fallbackReady)
                        record("Crate fixture for independent fire and depth cases",
                            fallbackReady and "PASS" or "FAIL", describe())
                        if fallbackReady then testFire() else finish() end
                    end)
                end), 2000)
                return
            end
            if mode ~= "crate" then testFire() return end
            bot.SetOrder(issued, true, false, true)
            waitForReady(10, function(reissued)
                record("Repeat order recognizes existing kit", reissued and "PASS" or "FAIL", describe())
                local _, morphine = inventory()
                if morphine == nil then finish() return end
                Entity.Spawner.AddItemToRemoveQueue(morphine)
                spawn("antidama1", stock or bot.WorldPosition)
                Timer.Wait(guarded(function()
                    waitForReady(45, function(refilled)
                        local counts = inventory()
                        record("One removed medicine is replenished",
                            refilled and counts.antidama1 == 1 and "PASS" or "FAIL", describe())
                        finish()
                    end)
                end), 2000)
            end)
        end)
    end), 1000)
end
Timer.Wait(guarded(function() start(20) end), 1000)
