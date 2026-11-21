-- Run in a disposable single-player quickstart.
LuaUserData.RegisterType("Barotrauma.GameMain")
LuaUserData.RegisterType("Barotrauma.DebugConsole")
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
local console = LuaUserData.CreateStatic("Barotrauma.DebugConsole")
local session = main.GameSession
assert(not main.Client and session and session.GameMode.Preset.Identifier.Value == "devsandbox",
    "Start a disposable single-player quickstart")

local lines = {}
local logPath = "LocalMods/Enhanced Experience/Tests/" ..
    (MaintenanceBatteryProbe and "MaintenanceBatteryProbe-results.txt" or "MaintenanceQuickstart-results.txt")
local function log(line)
    lines[#lines + 1] = line
    print("[Maintenance patrol] " .. line)
    File.Write(logPath, table.concat(lines, "\n") .. "\n")
end
local function guard(fn)
    return function(...)
        if main.GameSession ~= session then return end
        local ok, err = pcall(fn, ...)
        if not ok then log("ERROR " .. tostring(err)) end
    end
end
local function spawn(id, destination, fn)
    Entity.Spawner.AddItemToSpawnQueue(assert(ItemPrefab.Prefabs[Identifier(id)]), destination, nil, 0, guard(fn))
end

local function prepareBattery(battery)
    for connection in battery.Item.Connections do
        if connection.Name == "set_rate" then
            local wires = {}
            for wire in connection.Wires do wires[#wires + 1] = wire end
            for _, wire in ipairs(wires) do wire.RemoveConnection(connection) end
            log("Disconnected test battery rate input: " .. #wires .. " wire(s)")
        end
    end
    battery.RechargeSpeed = 0
    battery.Charge = battery.Capacity * 0.5
end

if MaintenanceBatteryProbe then
    MaintenanceBatteryProbe = nil
    local descriptor = LuaUserData.RegisterType("EnhancedExperience.MaintenancePatrol")
    LuaUserData.MakeMethodAccessible(descriptor, "CanAccess")
    local assistant, patrol
    for bot in Character.CharacterList do
        if bot.IsBot and bot.IsOnPlayerTeam and bot.Info and bot.Info.Job and
            bot.Info.Job.Prefab.Identifier.Value == "assistant" then
            for objective in bot.AIController.ObjectiveManager.Objectives do
                if objective.Identifier.Value == "maintenancepatrol" then assistant, patrol = bot, objective end
            end
        end
    end
    assert(patrol, "Run the maintenance quickstart fixture first")
    log("START")
    local target
    for item in Item.ItemList do
        local battery = item.GetComponentString("PowerContainer")
        if battery and item.Submarine == assistant.Submarine and item.HasTag(Identifier("battery")) and
            battery.MaxRechargeSpeed > 0 and not battery.OutputDisabled and patrol.CanAccess(item) then
            target = battery
            break
        end
    end
    assert(target, "No accessible battery for the fixture")
    log("Testing battery " .. target.Item.ID)
    prepareBattery(target)
    local function sampleBattery(attempt)
        if console.IsOpen then console.Toggle() end
        local objective = assistant.AIController.ObjectiveManager.CurrentObjective
        log(string.format("%ds %.1f%% rate=%.1f%% access=%s | AI %s", attempt * 5, target.ChargePercentage,
            target.RechargeRatio * 100, tostring(patrol.CanAccess(target.Item)),
            objective and objective.Identifier.Value or "-"))
        if target.ChargePercentage >= 99.9 and target.RechargeRatio > 0 then
            log("PASS")
        elseif attempt < 24 then
            Timer.Wait(guard(function() sampleBattery(attempt + 1) end), 5000)
        else
            log("FAIL")
        end
    end
    Timer.Wait(guard(function() sampleBattery(0) end), 500)
    return
end

local function begin(attempts)
    if console.IsOpen then console.Toggle() end
    local crew, station, tankStation, powered, repair = {}, nil, nil, nil, {}
    for bot in Character.CharacterList do
        if bot.IsBot and bot.IsOnPlayerTeam and bot ~= Character.Controlled and bot.CurrentHull then
            crew[bot.Info.Job.Prefab.Identifier.Value] = bot
        end
    end
    local assistant = crew.assistant
    if assistant then
        for item in Item.ItemList do
            if item.Submarine == assistant.Submarine and item.CurrentHull and item.ParentInventory == nil then
                local id = item.Prefab.Identifier.Value
                local container = item.GetComponentString("ItemContainer")
                local power = item.GetComponentString("Powered")
                if item.HasTag(Identifier("batterycellrecharger")) and container and power and power.Voltage > 0 then
                    station = item
                end
                if item.HasTag(Identifier("oxygentankrefiller")) and container and power and power.Voltage > 0 then
                    tankStation = item
                end
                local battery = item.GetComponentString("PowerContainer")
                if battery and item.HasTag(Identifier("battery")) and not battery.OutputDisabled and
                    battery.MaxRechargeSpeed > 0 and item.IsInteractable(assistant) and item.HasAccess(assistant) then
                    powered = battery
                end
                local function nearRepair(skill, bot)
                    if not bot or not item.HasAccess(bot) or not item.GetComponentString("Repairable") then return end
                    local function distance(target)
                        local dx = target.WorldPosition.X - bot.WorldPosition.X
                        local dy = target.WorldPosition.Y - bot.WorldPosition.Y
                        return dx * dx + dy * dy
                    end
                    if not repair[skill] or distance(item) < distance(repair[skill]) then repair[skill] = item end
                end
                if id == "junctionbox" then nearRepair("electrical", crew.engineer) end
                if id == "pump" or id == "smallpump" then nearRepair("mechanical", crew.mechanic) end
            end
        end
    end
    if not (assistant and station and powered and crew.engineer and crew.mechanic) then
        if attempts == 0 then
            log("ERROR missing fixture: assistant=" .. tostring(assistant) .. " engineer=" .. tostring(crew.engineer) ..
                " mechanic=" .. tostring(crew.mechanic) .. " station=" .. tostring(station) .. " battery=" .. tostring(powered))
        else Timer.Wait(guard(function() begin(attempts - 1) end), 1000) end
        return
    end
    local manager = assistant.AIController.ObjectiveManager
    local function objectiveName(bot)
        local objective = bot.AIController.ObjectiveManager.CurrentObjective
        return objective and objective.Identifier.Value or "-"
    end
    local hasPatrol = false
    for objective in manager.Objectives do
        if objective.Identifier.Value == "maintenancepatrol" then hasPatrol = true end
    end
    log("Assistant patrol installed: " .. tostring(hasPatrol))
    if not hasPatrol then return end
    for _, role in ipairs({ "engineer", "mechanic" }) do
        local rules = {}
        for objective in crew[role].AIController.ObjectiveManager.Objectives do
            local id = objective.Identifier.Value
            if id:find("repair", 1, true) then rules[#rules + 1] = id end
        end
        log(role .. " repair objectives: " .. table.concat(rules, ", "))
    end
    log("Testing battery " .. powered.Item.ID)
    prepareBattery(powered)
    for _, item in pairs(repair) do item.Condition = item.MaxCondition * 0.2 end
    local tests = {}
    local spot = station.WorldPosition
    local function resource(name, id, percent, destination)
        spawn(id, destination, function(item)
            item.Condition = item.MaxCondition * percent / 100
            tests[#tests + 1] = { name = name, item = item, origin = item.Container, initial = percent }
        end)
    end
    resource("ground 0", "batterycell", 0, spot)
    resource("ground 91", "batterycell", 91, spot)
    resource("ground 100", "batterycell", 100, spot)
    resource("reactor fuel 50", "fuelrod", 50, spot)
    resource("ammo 50", "coilgunammobox", 50, spot)
    if tankStation then
        resource("oxygen 50", "oxygentank", 50, tankStation.WorldPosition)
    else
        log("SKIP oxygen: no powered refiller found")
    end
    spawn("flashlight", spot, function(tool)
        resource("flashlight 50", "batterycell", 50, tool.GetComponentString("ItemContainer").Inventory)
    end)
    spawn("metalcrate", spot, function(crate)
        resource("crate 90", "batterycell", 90, crate.GetComponentString("ItemContainer").Inventory)
    end)
    local waitOrder = OrderPrefab.Prefabs[Identifier("wait")]
        .CreateInstance(OrderPrefab.OrderTargetType.Entity, Character.Controlled, false).WithManualPriority(3)
    local interrupted, dismissed, resumed, sawWait = false, false, false, false
    local witnessedFixer = {}
    local function sample(tick)
        local status = {}
        for _, test in ipairs(tests) do
            local item = test.item
            local parent = item.Container
            status[#status + 1] = string.format("%s=%.1f%%/%s", test.name, item.ConditionPercentage,
                parent and parent.Prefab.Identifier.Value or "-")
        end
        for skill, item in pairs(repair) do
            local fixer = item.GetComponentString("Repairable").CurrentFixer
            if fixer == (skill == "electrical" and crew.engineer or crew.mechanic) then
                witnessedFixer[skill] = true
            end
            status[#status + 1] = string.format("%s=%.1f%%/%s", skill, item.ConditionPercentage,
                fixer and fixer.Info.Job.Prefab.Identifier.Value or "-")
        end
        log(string.format("%ds %s | AI %s, %s, %s | battery %.1f%% at rate %.1f%%", tick,
            table.concat(status, " "), objectiveName(assistant), objectiveName(crew.engineer),
            objectiveName(crew.mechanic), powered.ChargePercentage, powered.RechargeRatio * 100))
        if not interrupted and objectiveName(assistant) == "maintenancepatrol" then
            interrupted = true
            assistant.SetOrder(waitOrder, true, false, true)
            Timer.Wait(guard(function()
                local active = objectiveName(assistant)
                sawWait = active == "wait"
                log("Direct order during patrol: " .. active)
                assistant.SetOrder(waitOrder.GetDismissal(), true, false, true)
                dismissed = true
            end), 5000)
        elseif dismissed and not resumed and
            objectiveName(assistant) == "maintenancepatrol" then
            resumed = true
            log("Assistant resumed patrol")
        end
        if tick < 165 then
            Timer.Wait(guard(function() sample(tick + 5) end), 5000)
            return
        end
        local function result(name, ok) log((ok and "PASS " or "FAIL ") .. name) end
        for _, test in ipairs(tests) do
            local eligible = test.name == "ground 0" or test.name == "flashlight 50" or
                test.name == "crate 90" or test.name == "oxygen 50"
            local expected = eligible and 100 or test.initial
            local restored = test.name ~= "crate 90" or test.item.Container == test.origin
            result(test.name, math.abs(test.item.ConditionPercentage - expected) < 0.05 and restored)
        end
        for skill, item in pairs(repair) do
            result(skill, witnessedFixer[skill] and item.IsFullCondition)
        end
        result("direct order interrupts patrol and dismissal resumes it", sawWait and resumed)
        result("stationary battery", powered.ChargePercentage >= 99.9 and powered.RechargeRatio > 0)
        log("COMPLETE")
    end
    Timer.Wait(guard(function() sample(5) end), 5000)
end

log("START")
local hasAssistant = false
local alternate
for bot in Character.CharacterList do
    if bot.IsBot and bot.IsOnPlayerTeam and bot ~= Character.Controlled and bot.Info and bot.Info.Job then
        local role = bot.Info.Job.Prefab.Identifier.Value
        if role == "assistant" then hasAssistant = true end
        if role == "securityofficer" then alternate = bot end
    end
end
if not hasAssistant and alternate then
    LuaUserData.RegisterType("Barotrauma.Job")
    LuaUserData.RegisterType("Barotrauma.JobPrefab")
    local job = LuaUserData.CreateStatic("Barotrauma.Job", true)
    local prefabs = LuaUserData.CreateStatic("Barotrauma.JobPrefab").Prefabs
    alternate.Info.Job = job(prefabs[Identifier("assistant")], false)
    alternate.AIController.ObjectiveManager.CreateAutonomousObjectives()
end
Timer.Wait(guard(function() begin(45) end), 500)
