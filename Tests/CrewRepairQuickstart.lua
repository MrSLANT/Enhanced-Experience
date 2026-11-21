-- Run only in a disposable single-player quickstart.
LuaUserData.RegisterType("Barotrauma.GameMain")
LuaUserData.RegisterType("Barotrauma.DebugConsole")
LuaUserData.RegisterType("Barotrauma.Structure")
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
local console = LuaUserData.CreateStatic("Barotrauma.DebugConsole")
local structure = LuaUserData.CreateStatic("Barotrauma.Structure")
local session = main.GameSession
assert(not main.Client and session and session.GameMode.Preset.Identifier.Value == "devsandbox",
    "Start a disposable single-player quickstart")
local edgeCases = CrewRepairEdgeCases == true
CrewRepairEdgeCases = nil
local resultFile = edgeCases and "CrewRepairEdgeCases-results.txt" or "CrewRepairQuickstart-results.txt"

local lines = {}
local function log(message)
    lines[#lines + 1] = message
    print("[Crew repair] " .. message)
    File.Write("LocalMods/Enhanced Experience/Tests/" .. resultFile, table.concat(lines, "\n") .. "\n")
end
local function later(callback, milliseconds)
    Timer.Wait(function()
        if main.GameSession ~= session then return end
        local ok, err = pcall(callback)
        if not ok then log("ERROR " .. tostring(err)) end
    end, milliseconds)
end
local function manager(bot) return bot.AIController.ObjectiveManager end
local function active(bot)
    local objective = manager(bot).CurrentObjective
    return objective and objective.Identifier.Value or "-"
end
local function distance(a, b)
    return (a.X - b.X)^2 + (a.Y - b.Y)^2
end
local function waitUntil(label, predicate, complete, remaining)
    if predicate() then
        log("PASS " .. label)
        complete()
    elseif remaining == 0 then
        log("FAIL " .. label)
    else
        later(function() waitUntil(label, predicate, complete, remaining - 1) end, 250)
    end
end

log("START")
later(function()
    if console.IsOpen then console.Toggle() end
    local crew = {}
    for bot in Character.CharacterList do
        if bot.IsBot and bot.IsOnPlayerTeam and bot ~= Character.Controlled and bot.CurrentHull and bot.Info.Job then
            crew[bot.Info.Job.Prefab.Identifier.Value] = bot
        end
    end
    local engineer = assert(crew.engineer, "Missing engineer")
    local mechanic = assert(crew.mechanic, "Missing mechanic")
    local assistant = crew.assistant
    if not assistant then
        assistant = assert(crew.securityofficer, "Missing assistant or substitute")
        LuaUserData.RegisterType("Barotrauma.Job")
        LuaUserData.RegisterType("Barotrauma.JobPrefab")
        local job = LuaUserData.CreateStatic("Barotrauma.Job", true)
        local prefabs = LuaUserData.CreateStatic("Barotrauma.JobPrefab").Prefabs
        assistant.Info.Job = job(prefabs[Identifier("assistant")], false)
        manager(assistant).CreateAutonomousObjectives()
    end
    local waitOrder = OrderPrefab.Prefabs[Identifier("wait")]
        .CreateInstance(OrderPrefab.OrderTargetType.Entity, Character.Controlled, false).WithManualPriority(3)

    local function nearestItem(bot, predicate)
        local best, nearest
        for item in Item.ItemList do
            if item.Submarine == bot.Submarine and item.CurrentHull and item.HasAccess(bot) and predicate(item) then
                local score = distance(item.WorldPosition, bot.WorldPosition)
                if not nearest or score < nearest then best, nearest = item, score end
            end
        end
        return assert(best, "Missing repair target")
    end
    local electrical = nearestItem(engineer, function(item) return item.Prefab.Identifier.Value == "junctionbox" end)
    local mechanical = nearestItem(mechanic, function(item)
        local id = item.Prefab.Identifier.Value
        return id == "pump" or id == "smallpump"
    end)
    local function openLeak(bot)
        local candidates = {}
        for wall in structure.WallList do
            if wall.Submarine == bot.Submarine and wall.MaxHealth > 0 then
                for index = 0, wall.SectionCount - 1 do
                    if wall.SectionDamage(index) == 0 then
                        local pos = wall.SectionPosition(index, true)
                        candidates[#candidates + 1] = {wall = wall, index = index, distance = distance(pos, bot.WorldPosition)}
                    end
                end
            end
        end
        table.sort(candidates, function(a, b) return a.distance < b.distance end)
        for _, candidate in ipairs(candidates) do
            local wall, index = candidate.wall, candidate.index
            wall.AddDamage(index, wall.MaxHealth * 0.6, nil, false, false)
            if wall.SectionIsLeakingFromOutside(index) then
                log("Opened leak " .. wall.ID .. "/" .. index)
                return function() return wall.SectionIsLeaking(index) end
            end
            wall.AddDamage(index, -wall.SectionDamage(index), nil, false, false)
        end
        error("No exterior leak could be created")
    end

    if edgeCases then
        local reactor = nearestItem(engineer, function(item) return item.HasTag(Identifier("reactor")) end)
        local position = reactor.WorldPosition
        engineer.AnimController.FindHull(position, true, true)
        engineer.AnimController.SetPosition(reactor.SimPosition, false, true, true, true)
        electrical = nearestItem(engineer, function(item) return item.Prefab.Identifier.Value == "junctionbox" end)
        local function markDamaged(item) item.Condition = item.MaxCondition * 0.2 end
        local function fixer(item) return item.GetComponentString("Repairable").CurrentFixer end
        local function diagnose(label, passed)
            log((passed and "PASS " or "FAIL ") .. label)
        end
        markDamaged(reactor)
        markDamaged(electrical)
        log(string.format("Both electrical targets at 20%%; reactor distance %.0f, junction distance %.0f, same hull: %s",
            math.sqrt(distance(reactor.WorldPosition, engineer.WorldPosition)),
            math.sqrt(distance(electrical.WorldPosition, engineer.WorldPosition)),
            tostring(reactor.CurrentHull == electrical.CurrentHull and engineer.CurrentHull == reactor.CurrentHull)))
        local function target(bot)
            local objective = manager(bot).CurrentObjective
            return objective and objective.Identifier.Value == "repairelectrical" and objective.CurrentSubObjective
        end
        waitUntil("engineer begins an electrical repair", function()
            return fixer(reactor) == engineer or fixer(electrical) == engineer
        end, function()
            for objective in manager(engineer).CurrentObjective.SubObjectives do
                if objective.Item == reactor or objective.Item == electrical then
                    log(string.format("Candidate %s: priority %.2f", objective.Item.Prefab.Identifier.Value,
                        objective.Priority))
                end
            end
            diagnose("reactor selected before junction box", fixer(reactor) == engineer)
            local repairing = fixer(reactor) == engineer and reactor or electrical
            local other = repairing == reactor and electrical or reactor
            other.Condition = other.MaxCondition
            local hull = assert(engineer.CurrentHull)
            local oldOxygen = hull.Oxygen
            local function oxygenTest(remaining)
                if active(engineer) == "find safety" or remaining == 0 then
                    diagnose("oxygen emergency interrupts electrical repair", active(engineer) == "find safety")
                    hull.Oxygen = oldOxygen
                    engineer.Oxygen = 100
                    later(function()
                        assistant.Info.Job = engineer.Info.Job
                        manager(assistant).CreateAutonomousObjectives()
                        markDamaged(repairing)
                        local sawOwner, duplicate = false, false
                        local function sample(steps)
                            local a, b = target(engineer), target(assistant)
                            if fixer(repairing) == engineer or fixer(repairing) == assistant then sawOwner = true end
                            if a and b and a.Priority > 0 and b.Priority > 0 and
                                a.Item == repairing and b.Item == repairing then duplicate = true end
                            if repairing.IsFullCondition or steps == 0 then
                                diagnose("two engineers finish the shared target without duplicate assignment",
                                    sawOwner and repairing.IsFullCondition and not duplicate)
                                log("COMPLETE")
                            else
                                later(function() sample(steps - 1) end, 100)
                            end
                        end
                        sample(450)
                    end, 2000)
                    return
                end
                hull.Oxygen = 0
                engineer.Oxygen = 5
                later(function() oxygenTest(remaining - 1) end, 100)
            end
            oxygenTest(80)
        end, 100)
        return
    end

    assistant.SetOrder(waitOrder, true, false, true)
    electrical.Condition = electrical.MaxCondition * 0.2
    mechanical.Condition = mechanical.MaxCondition * 0.2
    waitUntil("engineer and mechanic select their specialist tasks", function()
        return active(engineer) == "repairelectrical" and active(mechanic) == "repairmechanical"
    end, function()
        local leaking = openLeak(mechanic)
        waitUntil("new leak interrupts mechanical repair", function()
            return active(mechanic) == "fixleaks" and leaking()
        end, function()
            mechanic.SetOrder(waitOrder, true, false, true)
            waitUntil("direct Wait interrupts leak repair", function() return active(mechanic) == "wait" end, function()
                assistant.SetOrder(waitOrder.GetDismissal(), true, false, true)
                waitUntil("assistant takes the open leak after dismissal", function()
                    return active(assistant) == "fixleaks" and leaking()
                end, function()
                    waitUntil("leak is closed and assistant resumes other tasks", function()
                        return not leaking() and active(assistant) ~= "fixleaks"
                    end, function()
                        mechanic.SetOrder(waitOrder.GetDismissal(), true, false, true)
                        waitUntil("electrical and mechanical fixtures reach full condition", function()
                            return electrical.IsFullCondition and mechanical.IsFullCondition
                        end, function() log("COMPLETE") end, 160)
                    end, 160)
                end, 60)
            end, 24)
        end, 60)
    end, 60)
end, 500)
