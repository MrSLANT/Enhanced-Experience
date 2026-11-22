-- Disposable single-player fixture. Use quickstart Humpback, then:
-- cl_lua dofile([[LocalMods/Enhanced Experience/Tests/CrewDeconstructionQuickstart.lua]])
LuaUserData.RegisterType("Barotrauma.GameMain")
LuaUserData.RegisterType("Barotrauma.DebugConsole")
LuaUserData.RegisterType("Barotrauma.Items.Components.Deconstructor")
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
local console = LuaUserData.CreateStatic("Barotrauma.DebugConsole")
local session = main.GameSession
assert(not main.Client and session and session.GameMode.Preset.Identifier.Value == "devsandbox",
    "Use a disposable single-player quickstart")

local results = {}
local resultPath = "LocalMods/Enhanced Experience/Tests/CrewDeconstructionQuickstart-results.txt"
local writeResult = EnhancedExperienceQuickstartWriteResult or File.Write
local function record(name, success, detail)
    local line = name .. ": " .. (success == nil and "RUNNING" or success and "PASS" or "FAIL") ..
        (detail and " (" .. detail .. ")" or "")
    results[#results + 1] = line
    print("[Crew deconstruction] " .. line)
    writeResult(resultPath, table.concat(results, "\n") .. "\n")
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


local function order(id, priority)
    return assert(OrderPrefab.Prefabs[Identifier(id)]).CreateInstance(
        OrderPrefab.OrderTargetType.Entity, Character.Controlled, false).WithManualPriority(priority)
end

local function start(attempts)
    if console.IsOpen then console.Toggle() end
    local crew, jobs, device = {}, {}, nil
    for bot in Character.CharacterList do
        if bot.IsBot and bot.IsOnPlayerTeam and not bot.IsDead and bot ~= Character.Controlled and bot.CurrentHull then
            local job = bot.Info.Job.Prefab.Identifier.Value
            if not jobs[job] then
                jobs[job] = true
                crew[#crew + 1] = bot
            end
        end
    end
    for item in Item.ItemList do
        if item.Submarine == Submarine.MainSub and item.CurrentHull then
            local machine = item.GetComponentString("Deconstructor")
            if machine and machine.HasPower and not machine.IsActive and machine.InputContainer.Inventory.IsEmpty() then
                device = machine
                break
            end
        end
    end
    if #crew < 2 or not device or not Character.Controlled then
        assert(attempts > 0, "Powered idle deconstructor and two different bot professions required")
        Timer.Wait(guarded(function() start(attempts - 1) end), 1000)
        return
    end

    local typeName = "EnhancedExperience.CrewDeconstructionObjective"
    LuaUserData.RegisterType(typeName)
    local command, previous = order("deconstructitems", 3), order("wait", 2)
    for _, bot in ipairs(crew) do
        local objective = bot.AIController.ObjectiveManager.CreateObjective(command, 1)
        record("Shared objective for " .. bot.Info.Job.Prefab.Identifier.Value,
            LuaUserData.IsTargetType(objective, typeName))
    end

    local speed, minimum = device.DeconstructionSpeed, device.MinVoltage
    device.DeconstructionSpeed = 30
    local output = device.OutputContainer.Inventory
    local initialOutputs = {}
    for item in output.AllItems do initialOutputs[item.ID] = true end
    local position = device.Item.WorldPosition + Vector2(30, 10)

    local function runBatch(index, crate)
        local bot = crew[index]
        local manager = bot.AIController.ObjectiveManager
        local job = bot.Info.Job.Prefab.Identifier.Value
        local function hasOrder()
            for entry in manager.CurrentOrders do
                if entry.Identifier.Value == "deconstructitems" then return true end
            end
            return false
        end

        local ids = index == 1 and {"titanite", "aquaticpoppy", "oxygeniteshard", "antidama1"}
            or {"quartz", "saltvineseed", "tobaccobud", "antidama1"}
        local batch, pending = {}, #ids
        local marked
        for _, id in ipairs(ids) do
            local destination = id == "antidama1" and position or crate.OwnInventory
            spawn(id, destination, function(item)
                batch[#batch + 1] = item
                if id == "antidama1" then marked = item end
                pending = pending - 1
            end)
        end

        waitFor(10, function() return pending == 0 end, function(ready)
            assert(ready, "Could not spawn batch for " .. job)
            bot.SetOrder(previous, true, false, true)
            if index == 1 then device.MinVoltage = math.max(minimum, device.Voltage) + 100 end
            bot.SetOrder(command, true, false, true)

            Timer.Wait(guarded(function()
                if index == 1 then
                    record("Order survives temporary power loss", hasOrder() and not device.IsActive)
                    device.MinVoltage = minimum
                end
                local mark = order("deconstructthis", 3).WithTargetEntity(marked)
                assert(session.CrewManager.AddOrder(mark, nil), "Could not mark the control item")
                local function complete()
                    local products = {}
                    for item in output.AllItems do
                        if not initialOutputs[item.ID] then products[#products + 1] = item end
                    end
                    for _, item in ipairs(products) do Entity.Spawner.AddItemToRemoveQueue(item) end
                    for _, item in ipairs(batch) do
                        if not item.Removed then return false end
                    end
                    return true
                end

                waitFor(85, complete, function()
                    for _, item in ipairs(batch) do
                        record(job .. " processes " .. item.Prefab.Identifier.Value, item.Removed)
                    end
                    waitFor(12, function()
                        local current = manager.CurrentObjective
                        return not hasOrder() and current and current.Identifier.Value == "wait"
                    end, function(resumed)
                        record(job .. " loses the finished order", not hasOrder())
                        record(job .. " resumes the previous Wait order", resumed)
                        if index == 1 then
                            if hasOrder() then bot.SetOrder(command.GetDismissal(), true, false, true) end
                            runBatch(2, crate)
                            return
                        end
                        spawn("aquaticpoppy", crate.OwnInventory, function(late)
                            Timer.Wait(guarded(function()
                                record("Finished order is not reissued for new stock",
                                    not hasOrder() and not late.Removed and crate.OwnInventory.Contains(late))
                                device.DeconstructionSpeed = speed
                                device.MinVoltage = minimum
                                record("Fixture complete", true)
                            end), 6500)
                        end)
                    end)
                end)
            end), 5000)
        end)
    end

    spawn("metalcrate", position, function(crate) runBatch(1, crate) end)
end

record("Fixture", nil)
Timer.Wait(guarded(function() start(35) end), 1000)
