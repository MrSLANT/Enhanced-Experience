-- Run on a LuaCs server in a disposable multiplayer mission.
LuaUserData.RegisterType("Barotrauma.GameMain")
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
local session = main.GameSession
assert(main.Server and session and session.IsRunning and not session.RoundEnding and Submarine.MainSub,
    "Run this from the LuaCs server console in a live multiplayer mission")
assert(session.GameMode.Preset.Identifier.Value == "mission", "Use a disposable Mission round")
assert(EnhancedExperienceNetworkRepairSession ~= session, "Start a fresh mission for another run")

local sub = Submarine.MainSub
local crew = {}
for bot in Character.CharacterList do
    if bot.IsBot and bot.IsOnPlayerTeam and not bot.IsDead and bot.CurrentHull and bot.Submarine == sub and
        bot.Info and bot.Info.Job then
        crew[bot.Info.Job.Prefab.Identifier.Value] = bot
    end
end
local engineer = assert(crew.engineer, "An onboard engineer bot is required")
local mechanic = assert(crew.mechanic, "An onboard mechanic bot is required")

local function report(status, detail)
    local message = "[Network repair] " .. status .. " - " .. detail
    print(message)
    File.Write("LocalMods/Enhanced Experience/Tests/NetworkCrewRepair.log", message .. "\n")
end
local function hasInspection(bot)
    for objective in bot.AIController.ObjectiveManager.Objectives do
        if objective.Identifier.Value == "inspectequipment" then return true end
    end
    return false
end
if not hasInspection(engineer) or not hasInspection(mechanic) then
    report("FAIL", "Enhanced Experience objectives missing: engineer=" ..
        tostring(hasInspection(engineer)) .. ", mechanic=" .. tostring(hasInspection(mechanic)))
    return
end

local samples = 480

local function closestDevice(bot, matches)
    local closest, distance
    for item in Item.ItemList do
        if item.Submarine == sub and item.CurrentHull and item.CurrentHull.WaterPercentage < 10 and
            item.HasAccess(bot) and item.IsInteractable(bot) and matches(item.Prefab.Identifier.Value) then
            local x = bot.WorldPosition.X - item.WorldPosition.X
            local y = bot.WorldPosition.Y - item.WorldPosition.Y
            local score = x * x + y * y
            if not distance or score < distance then closest, distance = item, score end
        end
    end
    return closest
end

local junction = assert(closestDevice(engineer, function(id) return id == "junctionbox" end),
    "No junction box accessible to the engineer")
local mechanical = assert(closestDevice(mechanic, function(id)
    return id == "pump" or id == "smallpump" or id == "engine" or id == "largeengine"
end), "No accessible pump or engine in a dry compartment")
EnhancedExperienceNetworkRepairSession = session

report("RUNNING", "server damaged junction box " .. junction.ID .. " and " .. mechanical.Prefab.Identifier.Value .. " " .. mechanical.ID .. " to 20%")
junction.Condition = junction.MaxCondition * 0.2
mechanical.Condition = mechanical.MaxCondition * 0.2

local simultaneous = false
local concurrentAssignments = false
local junctionWorkers, mechanicalWorkers = {}, {}
local observations = {}
local function objectiveName(bot)
    local current = bot.AIController.ObjectiveManager.CurrentObjective
    return current and current.Identifier.Value or "-"
end
local function assignedWorker(item)
    for worker in Character.CharacterList do
        if worker.IsBot and worker.IsOnPlayerTeam and not worker.IsDead and worker.Submarine == sub then
            local objective = worker.AIController.ObjectiveManager.CurrentObjective
            local id = objective and objective.Identifier.Value
            if id == "repairelectrical" or id == "repairmechanical" or id == "repairsystems" then
                local task = objective.CurrentSubObjective
                if task and task.Item == item then return worker end
            end
        end
    end
end

local function distinctWorkers()
    for electrical in pairs(junctionWorkers) do
        for mechanicalWorker in pairs(mechanicalWorkers) do
            if electrical ~= mechanicalWorker then return true end
        end
    end
    return false
end

local function sample(remaining)
    if main.GameSession ~= session or session.RoundEnding then return end
    local a = junction.GetComponentString("Repairable").CurrentFixer
    local b = mechanical.GetComponentString("Repairable").CurrentFixer
    if a and a.IsBot then junctionWorkers[a.ID] = true end
    if b and b.IsBot then mechanicalWorkers[b.ID] = true end
    simultaneous = simultaneous or (a ~= nil and b ~= nil and a ~= b and a.IsBot and b.IsBot)
    local assignedA, assignedB = assignedWorker(junction), assignedWorker(mechanical)
    concurrentAssignments = concurrentAssignments or (assignedA ~= nil and assignedB ~= nil and assignedA ~= assignedB)
    if remaining % 20 == 0 then
        observations[#observations + 1] = string.format("%ds junction #%d %.1f%% (%s), mechanical #%d %.1f%% (%s)",
            (samples - remaining) / 4, junction.ID, junction.ConditionPercentage, objectiveName(engineer),
            mechanical.ID, mechanical.ConditionPercentage, objectiveName(mechanic))
    end
    if junction.IsFullCondition and mechanical.IsFullCondition then
        local passed = distinctWorkers() and (concurrentAssignments or simultaneous)
        report(passed and "PASS" or "FAIL", string.format(
            "both devices reached 100%%; distinct repairers=%s; concurrent assignments=%s; simultaneous physical repair=%s\n%s",
            tostring(distinctWorkers()), tostring(concurrentAssignments), tostring(simultaneous), table.concat(observations, "\n")))
    elseif remaining == 0 then
        report("FAIL", string.format("junction %.1f%%, mechanical %.1f%%, simultaneous=%s\n%s",
            junction.ConditionPercentage, mechanical.ConditionPercentage, tostring(simultaneous), table.concat(observations, "\n")))
    else
        Timer.Wait(function() sample(remaining - 1) end, 250)
    end
end
sample(samples)
