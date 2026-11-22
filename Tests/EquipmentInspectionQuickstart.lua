-- Run through Run-Quickstart.lua in a fresh single-player round.
local writeResult = assert(EnhancedExperienceQuickstartWriteResult)
local lines = {}
local checkReport = EquipmentInspectionReport == true
EquipmentInspectionReport = nil
local function log(message)
    lines[#lines + 1] = message
    print("[Equipment inspection] " .. message)
    writeResult(nil, table.concat(lines, "\n") .. "\n")
end

local initialRooms = {}
for bot in Character.CharacterList do
    local job = bot.Info and bot.Info.Job
    local role = job and job.Prefab.Identifier.Value
    if bot ~= Character.Controlled and bot.IsBot and bot.IsOnPlayerTeam and bot.CurrentHull and
        (role == "mechanic" or role == "engineer") then
        local manager = bot.AIController.ObjectiveManager
        initialRooms[#initialRooms + 1] = { bot = bot, room = bot.CurrentHull, manager = manager }
    end
end
assert(#initialRooms > 0, "Keep an engineer or mechanic under bot control")
log("START")
local function active(entry, name)
    local objective = entry.manager.CurrentObjective
    return objective and objective.Identifier.Value == name
end
local function checkWait(entry, attempts)
    if active(entry, "wait") then
        log("PASS direct Wait interrupts inspection")
        log("COMPLETE")
    elseif attempts == 0 then
        log("FAIL direct Wait interrupts inspection")
        log("COMPLETE")
    else
        Timer.Wait(function() checkWait(entry, attempts - 1) end, 250)
    end
end

local function sampleReport()
    LuaUserData.RegisterType("Barotrauma.GameMain")
    local game = LuaUserData.CreateStatic("Barotrauma.GameMain")
    local crewManager = game.GameSession.CrewManager

    local function waitForReport(item, hullId, attempts)
        for entry in crewManager.ActiveOrders do
            local order = entry.Order
            local target = order.TargetEntity
            if order.Identifier.Value == "reportbrokendevices" and target and target.ID == item.ID then
                log(string.format("PASS native broken-device report targets equipment #%d in compartment #%d", item.ID, hullId))
                log("COMPLETE")
                return
            end
        end
        if attempts == 0 then
            log(string.format("Equipment #%d condition at end: %.1f%%", item.ID, item.ConditionPercentage))
            log("FAIL native broken-device report for the inspected compartment")
            log("COMPLETE")
        else
            Timer.Wait(function() waitForReport(item, hullId, attempts - 1) end, 250)
        end
    end

    local function waitForRoute(attempts)
        for _, entry in ipairs(initialRooms) do
            local hull = entry.bot.CurrentHull
            if active(entry, "inspectequipment") and hull and hull.ID ~= entry.room.ID then
                for item in Item.RepairableItems do
                    if item.CurrentHull == hull and item.IsFullCondition and item.HasAccess(entry.bot) and
                        item.IsInteractable(entry.bot) and not item.IgnoreByAI(entry.bot) then
                        local repairable = item.GetComponentString("Repairable")
                        if repairable and repairable.RepairThreshold > 5 then
                            item.Condition = item.MaxCondition * 0.05
                            log(string.format("Damaged %s #%d after inspector entered compartment #%d",
                                item.Prefab.Identifier.Value, item.ID, hull.ID))
                            waitForReport(item, hull.ID, 160)
                            return
                        end
                    end
                end
            end
        end
        if attempts == 0 then
            log("FAIL no inspection reached a separate compartment with reportable equipment")
            log("COMPLETE")
        else
            Timer.Wait(function() waitForRoute(attempts - 1) end, 250)
        end
    end
    waitForRoute(240)
end

local function sample(attempts)
    for _, entry in ipairs(initialRooms) do
        if active(entry, "inspectequipment") then
            entry.sawInspection = true
        end
        if entry.sawInspection and entry.bot.CurrentHull and entry.bot.CurrentHull ~= entry.room then
            log("PASS specialist visits another compartment during inspection")
            local order = OrderPrefab.Prefabs[Identifier("wait")]
                .CreateInstance(OrderPrefab.OrderTargetType.Entity, Character.Controlled, false).WithManualPriority(3)
            entry.bot.SetOrder(order, true, false, true)
            checkWait(entry, 24)
            return
        end
    end
    if attempts == 0 then
        log("FAIL specialist visits another compartment during inspection")
        log("COMPLETE")
    else
        Timer.Wait(function() sample(attempts - 1) end, 250)
    end
end
if checkReport then sampleReport() else sample(240) end
