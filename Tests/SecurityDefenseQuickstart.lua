-- Run only in a fresh disposable single-player quickstart.
LuaUserData.RegisterType("Barotrauma.GameMain")
LuaUserData.RegisterType("Barotrauma.DebugConsole")
LuaUserData.RegisterType("Barotrauma.Hull")
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
local console = LuaUserData.CreateStatic("Barotrauma.DebugConsole")
local hull = LuaUserData.CreateStatic("Barotrauma.Hull")
local turretType = LuaUserData.RegisterType("Barotrauma.Items.Components.Turret")
LuaUserData.MakeMethodAccessible(turretType, "CheckLineOfSight")
local session = main.GameSession
assert(not main.Client and session and session.GameMode.Preset.Identifier.Value == "devsandbox", "Run in quickstart")
local extended = SecurityDefenseExtended == true
local shipTest = SecurityDefenseShipTest == true
local railTest = SecurityDefenseRailTest == true
SecurityDefenseExtended = nil
SecurityDefenseShipTest = nil
SecurityDefenseRailTest = nil
local resultFile = shipTest and "SecurityDefenseShip-results.txt" or
    railTest and "SecurityDefenseRail-results.txt" or
    extended and "SecurityDefenseExtended-results.txt" or "SecurityDefenseQuickstart-results.txt"

local lines = {}
local function log(message)
    lines[#lines + 1] = message
    print("[Security defense] " .. message)
    File.Write("LocalMods/Enhanced Experience/Tests/" .. resultFile,
        table.concat(lines, "\n") .. "\n")
end
local function guarded(callback)
    return function(...)
        if main.GameSession ~= session then return end
        local ok, err = pcall(callback, ...)
        if not ok then log("ERROR " .. tostring(err)) end
    end
end
local function later(milliseconds, callback) Timer.Wait(guarded(callback), milliseconds) end
local function await(label, predicate, callback, remaining, onTimeout)
    if predicate() then
        log("PASS " .. label)
        callback()
    elseif remaining <= 0 then
        log("FAIL " .. label)
        if onTimeout then onTimeout() end
    else
        later(250, function() await(label, predicate, callback, remaining - 250, onTimeout) end)
    end
end
local function manager(bot) return bot.AIController.ObjectiveManager end
local function active(bot)
    local objective = manager(bot).CurrentObjective
    return objective and objective.Identifier.Value or "-"
end

log("START")
later(500, function()
    if console.IsOpen then console.Toggle() end
    local submarine = assert(Submarine.MainSub)
    local security, substitute
    for bot in Character.CharacterList do
        if bot.IsBot and bot.IsOnPlayerTeam and bot ~= Character.Controlled and bot.Submarine == submarine and bot.Info.Job then
            local role = bot.Info.Job.Prefab.Identifier.Value
            if role == "securityofficer" then security = bot end
            if role == "medicaldoctor" then substitute = bot end
        end
    end
    assert(security, "No security officer")
    local weaponRoot, intruderRoot = false, false
    for objective in manager(security).Objectives do
        if objective.Identifier.Value == "operateweapons" then weaponRoot = true end
        if objective.Identifier.Value == "fightintruders" then intruderRoot = true end
    end
    assert(weaponRoot and intruderRoot, "New plugin objectives missing; restart Barotrauma")
    log("PASS automatic objectives installed")

    local stations, railgun = {}, nil
    for item in Item.ItemList do
        if item.Submarine == submarine then
            local turret = item.GetComponentString("Turret")
            if turret and item.Condition > 0 then
                if item.Prefab.Identifier.Value == "railgun" then railgun = turret
                else stations[#stations + 1] = turret end
            end
        end
    end
    assert(#stations > 0, "Submarine needs a non-railgun turret")
    local function testRailgun(onComplete, maintainTarget)
        if not railgun or not railgun.Item.FindController() then
            log("SKIP direct Railgun order: no wired railgun on this submarine")
            onComplete()
            return
        end
        local controller = railgun.Item.FindController()
        LuaUserData.RegisterType("Barotrauma.Order")
        local newOrder = LuaUserData.CreateStatic("Barotrauma.Order", true)
        local order = newOrder(OrderPrefab.Prefabs[Identifier("operateweapons")],
            railgun.Item, railgun, Character.Controlled, false).WithManualPriority(3)
        security.SetOrder(order, true, false, true)
        local function finish()
            security.SetOrder(order.GetDismissal(), true, false, true)
            onComplete()
        end
        await("direct Railgun order takes control of its periscope", function()
            if maintainTarget then maintainTarget() end
            return active(security) == "operateweapons" and security.SelectedItem == controller.Item
        end, finish, 20000, finish)
    end
    if railTest then
        testRailgun(function() log("COMPLETE") end)
        return
    end

    local best, bestCount, points = nil, nil, {}
    local function clear(turret, point)
        local origin = turret.Item.WorldPosition
        return turret.IsWithinAimingRadius(point) and
            not turret.CheckLineOfSight(Vector2(origin.X / 100, origin.Y / 100), Vector2(point.X / 100, point.Y / 100))
    end
    if shipTest then
        LuaUserData.RegisterType("Barotrauma.SubmarineInfo")
        LuaUserData.RegisterType("Barotrauma.Structure")
        local info = LuaUserData.CreateStatic("Barotrauma.SubmarineInfo")
        local structure = LuaUserData.CreateStatic("Barotrauma.Structure")
        local fixture, point, attackerHull, hitWall, firingTurret
        for saved in info.SavedSubmarines do
            if saved.Name == "Hemulen" then fixture = saved break end
        end
        assert(fixture, "Missing vanilla Hemulen shuttle")
        for _, turret in ipairs(stations) do
            local origin = turret.Item.WorldPosition
            for _, degree in ipairs({90, 270, 60, 120, 240, 300, 0, 180}) do
                local angle = math.rad(degree)
                local candidate = Vector2(origin.X + 2600 * math.cos(angle), origin.Y + 2600 * math.sin(angle))
                local range = turret.AIRange * turret.Item.OffsetOnSelectedMultiplier
                local controller = turret.Item.FindController()
                if range >= 2600 and controller and controller.Item.HasAccess(security) and
                    turret.HasPowerToShoot() and clear(turret, candidate) then
                    point, firingTurret = candidate, turret
                    break
                end
            end
            if point then break end
        end
        assert(point, "No clear sector to place the shuttle")
        local ship = assert(Submarine.Load(fixture, false, nil))
        ship.SetPosition(point, nil, true)
        ship.TeamID = CharacterTeamType.Team2
        local offset = Vector2(point.X - submarine.WorldPosition.X, point.Y - submarine.WorldPosition.Y)
        log("Shuttle placed in range of powered " .. firingTurret.Item.Prefab.Identifier.Value)
        local function holdShuttlePosition()
            local expected = Vector2(submarine.WorldPosition.X + offset.X, submarine.WorldPosition.Y + offset.Y)
            local actual = ship.WorldPosition
            if (actual.X - expected.X)^2 + (actual.Y - expected.Y)^2 > 150^2 then
                ship.SetPosition(expected, nil, true)
            end
        end
        for room in hull.HullList do
            if room.Submarine == ship then attackerHull = room break end
        end
        for wall in structure.WallList do
            if wall.Submarine == submarine and wall.HasBody and not wall.Indestructible and wall.SectionCount > 0 then
                hitWall = wall
                break
            end
        end
        assert(attackerHull and hitWall, "Missing attack fixture hull")
        local function foreignDamage()
            local damage = 0
            for wall in structure.WallList do
                if wall.Submarine == ship and wall.HasBody then
                    for i = 0, wall.SectionCount - 1 do damage = damage + wall.SectionDamage(i) end
                end
            end
            return damage
        end
        Entity.Spawner.AddCharacterToSpawnQueue(Identifier("human"), attackerHull.WorldPosition, guarded(function(attacker)
            attacker.TeamID = CharacterTeamType.Team2
            attacker.SetStun(40, true, true)
            local before = foreignDamage()
            later(4000, function()
                assert(attacker.Submarine == ship, "Attack source did not remain inside the shuttle")
                local afterIdle = foreignDamage()
                log((afterIdle <= before and "PASS " or "FAIL ") .. "foreign shuttle is undamaged before aggression")
                local ownBefore = hitWall.SectionDamage(0)
                hitWall.AddDamage(0, 5, attacker, false, false)
                assert(hitWall.SectionDamage(0) > ownBefore, "No attributed damage was applied to the player submarine")
                log("Applied hull damage attributed to the foreign crew")
                local lastStatus
                await("automatic turret hits the shuttle after attributed hull damage", function()
                    holdShuttlePosition()
                    local selected = security.SelectedItem
                    local objective = manager(security).CurrentObjective
                    local child = objective and objective.CurrentSubObjective
                    local status = active(security) .. "/" .. (child and child.Identifier.Value or "-") ..
                        "; selected=" .. (selected and selected.Prefab.Identifier.Value or "-") ..
                        "; damage=" .. string.format("%.1f", foreignDamage())
                    if status ~= lastStatus then log(status) lastStatus = status end
                    return active(security) == "operateweapons" and foreignDamage() > afterIdle
                end, function() log("COMPLETE") end, 30000)
            end)
        end))
        return
    end
    for _, turret in ipairs(stations) do
        local origin = turret.Item.WorldPosition
        for degree = 0, 345, 15 do
            local angle = math.rad(degree)
            local point = Vector2(origin.X + 950 * math.cos(angle), origin.Y + 950 * math.sin(angle))
            if clear(turret, point) and not hull.FindHull(point, nil, true, false) then
                points[#points + 1] = point
                local count = 0
                for _, other in ipairs(stations) do
                    if clear(other, point) then count = count + 1 end
                end
                if not bestCount or count > bestCount then best, bestCount = point, count end
            end
        end
    end
    assert(best, "No exterior point in any turret arc")
    log("Spawned spot has " .. bestCount .. " unobstructed turret arc(s)")
    local offsetX, offsetY = best.X - submarine.WorldPosition.X, best.Y - submarine.WorldPosition.Y
    Entity.Spawner.AddCharacterToSpawnQueue(Identifier("crawler"), best, guarded(function(enemy)
        enemy.SetStun(40, true, true)
        local initialVitality = enemy.Vitality
        local function moveExteriorPosition(point)
            offsetX, offsetY = point.X - submarine.WorldPosition.X, point.Y - submarine.WorldPosition.Y
        end
        local function holdExteriorPosition()
            if enemy.IsDead or enemy.Removed then return end
            local position = Vector2(submarine.WorldPosition.X + offsetX, submarine.WorldPosition.Y + offsetY)
            local actual = enemy.WorldPosition
            if (actual.X - position.X)^2 + (actual.Y - position.Y)^2 > 40^2 then
                enemy.AnimController.FindHull(position, true, true)
                enemy.AnimController.SetPosition(Vector2(position.X / 100, position.Y / 100), false, true, true, true)
                enemy.AnimController.Collider.LinearVelocity = Vector2(0, 0)
            end
        end
        local lastState
        local function state()
            local selected = security.SelectedItem
            local objective = manager(security).CurrentObjective
            local child = objective and objective.CurrentSubObjective
            local task = child and child.CurrentSubObjective
            local snapshot = active(security) .. "/" .. (child and child.Identifier.Value or "-") ..
                "/" .. (task and task.Identifier.Value or "-") ..
                " selected=" .. (selected and selected.Prefab.Identifier.Value or "-") ..
                " enemyInside=" .. tostring(enemy.CurrentHull ~= nil)
            if snapshot ~= lastState then
                log(snapshot)
                lastState = snapshot
            end
        end
        await("officer takes an armed periscope and hits the exterior crawler", function()
            holdExteriorPosition()
            state()
            return active(security) == "operateweapons" and security.SelectedItem and
                security.SelectedItem.HasTag(Identifier("periscope")) and enemy.Vitality < initialVitality
        end, function()
            if extended then
                enemy.GodMode = true
                local firstScope = security.SelectedItem
                local firstTurret
                for _, turret in ipairs(stations) do
                    local controller = turret.Item.FindController()
                    if controller and controller.Item == firstScope then firstTurret = turret end
                end
                local function railgunAfterSector()
                    testRailgun(function()
                        Entity.Spawner.AddEntityToRemoveQueue(enemy)
                        log("COMPLETE")
                    end, holdExteriorPosition)
                end
                local function testSector()
                    local alternate
                    if firstTurret then
                        for _, point in ipairs(points) do
                            if not firstTurret.IsWithinAimingRadius(point) then
                                for _, turret in ipairs(stations) do
                                    if turret ~= firstTurret and turret.Item.FindController() and
                                        turret.HasPowerToShoot() and clear(turret, point) then
                                        alternate = point
                                        break
                                    end
                                end
                            end
                            if alternate then break end
                        end
                    end
                    if alternate then
                        moveExteriorPosition(alternate)
                        await("target leaves original arc and officer occupies a different periscope", function()
                            holdExteriorPosition()
                            return active(security) == "operateweapons" and security.SelectedItem and
                                security.SelectedItem ~= firstScope and security.SelectedItem.HasTag(Identifier("periscope"))
                        end, railgunAfterSector, 25000, railgunAfterSector)
                    else
                        log("SKIP sector change: no clear point outside the original turret arc")
                        railgunAfterSector()
                    end
                end
                local ammo, loaders = {}, {}
                if firstTurret and not firstTurret.LaunchWithoutProjectile then
                    for linked in firstTurret.Item.linkedTo do
                        if linked.GetComponentString and firstTurret.Item.Prefab.IsLinkAllowed(linked.Prefab) and
                            linked.GetComponentString("ItemContainer") then
                            loaders[linked] = true
                            for item in linked.ContainedItems do ammo[#ammo + 1] = item end
                        end
                    end
                end
                if #ammo == 0 then
                    log("SKIP ammo replenishment: no loaded compatible ammunition in linked loaders")
                    testSector()
                else
                    local conditions = {}
                    for _, turret in ipairs(stations) do
                        if turret ~= firstTurret then
                            conditions[turret.Item] = turret.Item.Condition
                            turret.Item.Condition = 0
                        end
                    end
                    for _, item in ipairs(ammo) do item.Drop(nil, true, false) end
                    local function afterAmmo()
                        for item, condition in pairs(conditions) do item.Condition = condition end
                        testSector()
                    end
                    await("officer reloads the empty linked turret loader", function()
                        holdExteriorPosition()
                        for _, item in ipairs(ammo) do
                            if item.Container and loaders[item.Container] then return true end
                        end
                        return false
                    end, afterAmmo, 30000, afterAmmo)
                end
                return
            end
            assert(substitute, "No substitute officer for split-defense test")
            substitute.Info.Job = security.Info.Job
            manager(substitute).CreateAutonomousObjectives()
            enemy.GodMode = true
            local besideOfficer = Vector2(substitute.WorldPosition.X + 35, substitute.WorldPosition.Y)
            Entity.Spawner.AddCharacterToSpawnQueue(Identifier("crawler"), besideOfficer, guarded(function(intruder)
                    intruder.SetStun(40, true, true)
                    intruder.GodMode = true
                    local splitStatus
                    await("two officers split gunnery and intruder defense", function()
                        holdExteriorPosition()
                        local first, second = active(security), active(substitute)
                        local status = first .. "/" .. second .. "; intruder aboard=" ..
                            tostring(intruder.CurrentHull ~= nil and intruder.Submarine == submarine)
                        if status ~= splitStatus then
                            log(status)
                            splitStatus = status
                        end
                        return first == "operateweapons" and second == "fightintruders" or
                            first == "fightintruders" and second == "operateweapons"
                    end, function()
                        local gunner = active(security) == "operateweapons" and security or substitute
                        local waitOrder = OrderPrefab.Prefabs[Identifier("wait")]
                            .CreateInstance(OrderPrefab.OrderTargetType.Entity, Character.Controlled, false)
                            .WithManualPriority(3)
                        gunner.SetOrder(waitOrder, true, false, true)
                        await("direct Wait takes priority over gunnery", function() return active(gunner) == "wait" end,
                            function()
                                Entity.Spawner.AddEntityToRemoveQueue(intruder)
                                Entity.Spawner.AddEntityToRemoveQueue(enemy)
                                gunner.SetOrder(waitOrder.GetDismissal(), true, false, true)
                                await("both officers return to ordinary tasks after threats are removed", function()
                                    local first, second = active(security), active(substitute)
                                    return first ~= "operateweapons" and first ~= "fightintruders" and
                                        second ~= "operateweapons" and second ~= "fightintruders" and active(gunner) ~= "wait"
                                end, function() log("COMPLETE") end, 8000)
                            end, 6000)
                    end, 15000)
                end))
        end, 30000)
    end))
end)
