-- Run only in a disposable single-player quickstart.
local root = "LocalMods/Enhanced Experience/Tests/"
local edgeCases = ExteriorFollowEdgeCases == true
ExteriorFollowEdgeCases = nil
local resultFile = edgeCases and "ExteriorFollowEdgeCases-results.txt" or "ExteriorFollowQuickstart-results.txt"
local results = {}
local function report(name, pass, detail)
    local line = name .. ": " .. (pass and "PASS" or "FAIL") .. (detail and " (" .. detail .. ")" or "")
    results[#results + 1] = line
    print("[Exterior follow] " .. line)
    File.Write(root .. resultFile, table.concat(results, "\n") .. "\n")
end
local function guard(fn)
    return function(...)
        local ok, err = pcall(fn, ...)
        if not ok then report("Test fixture", false, tostring(err)) end
    end
end
local function after(ms, fn) Timer.Wait(guard(fn), ms) end
local function spawn(id, destination, fn)
    Entity.Spawner.AddItemToSpawnQueue(assert(ItemPrefab.Prefabs[Identifier(id)]), destination, nil, 0, guard(fn))
end
local function component(item, kind)
    return assert(item.GetComponentString(kind), kind)
end
local function offset(position, x, y) return Vector2(position.X + x, position.Y + y) end
local function distance(a, b)
    local x, y = a.WorldPosition.X - b.WorldPosition.X, a.WorldPosition.Y - b.WorldPosition.Y
    return math.sqrt(x * x + y * y)
end
local function move(character, position)
    character.AnimController.FindHull(position, true, true)
    character.AnimController.SetPosition(Vector2(position.X / 100, position.Y / 100), false, true, true, true)
    character.AnimController.Collider.LinearVelocity = Vector2(0, 0)
end
local main = LuaUserData.CreateStatic("Barotrauma.GameMain")
assert(main.Client == nil and main.GameSession and Submarine.MainSub and
    main.GameSession.GameMode.Preset.Identifier.Value == "devsandbox", "Run in single-player quickstart")
local leader = assert(Character.Controlled)
local bot, second
for candidate in Character.CharacterList do
    if candidate ~= leader and candidate.IsBot and candidate.TeamID == leader.TeamID and candidate.Inventory then
        if bot then second = candidate break end
        bot = candidate
    end
end
assert(bot, "No crew bot")
report("Quickstart and crew", true, bot.Name)
local manager = bot.AIController.ObjectiveManager
local base = offset(Submarine.MainSub.WorldPosition, edgeCases and -380 or 100, 650)
local order = OrderPrefab.Prefabs[Identifier("follow")].CreateInstance(OrderPrefab.OrderTargetType.Entity, leader, false)
    .WithManualPriority(3)
local function current() return manager.CurrentObjective and tostring(manager.CurrentObjective.DebugTag) or "none" end
local function wearer(character, nextStep)
    local inventory = character.Inventory
    local outer = inventory.FindLimbSlot(InvSlotType.OuterClothes)
    local old = inventory.GetItemInLimbSlot(InvSlotType.OuterClothes)
    if old then Entity.Spawner.AddItemToRemoveQueue(old) end
    spawn("abyssdivingsuit", character.WorldPosition, function(suit)
        spawn("oxygentank", suit.OwnInventory, function(_)
            assert(inventory.TryPutItem(suit, outer, false, false, character), "Could not equip pressure suit")
            if edgeCases then character.Oxygen = 100 end
            nextStep()
        end)
    end)
end
local function specimen(id, position, fn)
    spawn(id, position, function(item)
        local hold = component(item, "Holdable")
        hold.AttachToWall()
        component(item, "LevelResource").IsActive = true
        fn(item)
    end)
end
local function attack(interrupted)
    report("Bot is gathering as the enemy arrives", bot.PickingItem == interrupted, current())
    Entity.Spawner.AddCharacterToSpawnQueue(Identifier("crawler"), offset(leader.WorldPosition, 180, 0),
        guard(function(enemy)
            after(1500, function()
                local tag = current()
                report("Nearby creature preempts active gathering", tag:lower():find("combat", 1, true) ~= nil and
                    bot.PickingItem ~= interrupted and interrupted.ParentInventory == nil, tag)
                Entity.Spawner.AddEntityToRemoveQueue(enemy)
                after(2500, function()
                    local tagAfter = current()
                    report("Returns to follow after threat removal", tagAfter:lower():find("follow", 1, true) ~= nil, tagAfter)
                    bot.SetOrder(order.GetDismissal(), true, false, true)
                    after(600, function()
                        local hasFollow = false
                        for entry in manager.CurrentOrders do
                            if entry.Identifier.Value == "follow" then hasFollow = true end
                        end
                        report("Dismissal clears follow", not hasFollow, current())
                    end)
                end)
            end)
        end))
end
local function interruptPlants()
    move(leader, base)
    move(bot, offset(base, 0, -65))
    specimen("aquaticpoppy", offset(base, -20, -20), function(source)
        specimen("yeastshroom", offset(base, 25, -62), function(other)
            after(650, function()
                source.TryInteract(leader, false, true, false)
                after(1500, function() attack(other) end)
            end)
        end)
    end)
end
local function plants()
    move(leader, base)
    move(bot, offset(base, 0, -65))
    specimen("aquaticpoppy", offset(base, -20, -20), function(source)
        specimen("yeastshroom", offset(base, 25, -62), function(other)
            after(700, function()
                local idle = other.ParentInventory == nil and component(other, "Holdable").Attached
                report("Nearby plant alone does not trigger collection", idle, current())
                assert(leader.CanInteractWith(source, false), "Player cannot reach test plant")
                source.TryInteract(leader, false, true, false)
                after(250, function()
                    report("Native player plant interaction starts", leader.PickingItem == source, current())
                end)
                after(7000, function()
                    report("Bot harvests and retains a different plant", other.ParentInventory == bot.Inventory,
                        current() .. ", attached=" .. tostring(component(other, "Holdable").Attached))
                    base = offset(base, 250, 0)
                    interruptPlants()
                end)
            end)
        end)
    end)
end
local function mining()
    local repair = LuaUserData.RegisterType("Barotrauma.Items.Components.RepairTool")
    local resource = LuaUserData.RegisterType("Barotrauma.Items.Components.LevelResource")
    LuaUserData.MakeMethodAccessible(repair, "FixBody")
    LuaUserData.MakeFieldAccessible(resource, "trigger")
    move(leader, base)
    move(bot, offset(base, 0, -68))
    spawn("plasmacutter", leader.Inventory, function(playerCutter)
        local hand = leader.Inventory.FindLimbSlot(InvSlotType.RightHand)
        assert(leader.Inventory.TryPutItem(playerCutter, hand, false, false, leader), "Could not equip test player cutter")
        spawn("plasmacutter", bot.Inventory, function(botCutter)
            spawn("oxygentank", botCutter.OwnInventory, function(fuel)
                specimen("fulgurium", offset(base, -45, 10), function(source)
                    specimen("incendium", offset(base, 45, -75), function(other)
                        local tool = component(playerCutter, "RepairTool")
                        local playerOre = component(source, "LevelResource")
                        local botOre = component(other, "LevelResource")
                        local fuelBefore = fuel.Condition
                        local function cutOnce()
                            if playerOre.Attached then
                                tool.FixBody(leader, source.SimPosition, 0.2, 1, playerOre.trigger.FarseerBody)
                            end
                        end
                        local function cutFor(remaining)
                            cutOnce()
                            if remaining > 0 then after(200, function() cutFor(remaining - 1) end) end
                        end
                        after(800, function()
                            cutFor(24)
                            after(1000, function()
                                report("Native ore hit advances player deposit", playerOre.DeattachTimer > 0 or
                                    not playerOre.Attached, "progress=" .. tostring(playerOre.DeattachTimer))
                            end)
                            after(8500, function()
                                report("Bot cuts and retains a different ore", other.ParentInventory == bot.Inventory and
                                    fuel.Condition < fuelBefore, "attached=" .. tostring(botOre.Attached) ..
                                    ", progress=" .. tostring(botOre.DeattachTimer) .. ", fuel=" .. tostring(fuel.Condition))
                                base = offset(base, 300, 0)
                                plants()
                            end)
                        end)
                    end)
                end)
            end)
        end)
    end)
end
local function testOxygen()
    base = offset(base, 320, 0)
    move(leader, base)
    move(bot, offset(base, 0, -68))
    second.SetOrder(order.GetDismissal(), true, false, true)
    specimen("aquaticpoppy", offset(base, -20, -20), function(source)
        specimen("yeastshroom", offset(base, 25, -62), function(other)
            after(1200, function()
                move(leader, offset(source.WorldPosition, -15, 10))
                move(bot, offset(other.WorldPosition, -15, 10))
                bot.Oxygen = 100
                source.TryInteract(leader, false, true, false)
                after(1500, function()
                    local started = bot.PickingItem == other
                    report("Bot gathers before simulated oxygen emergency", started, current() ..
                        ", player=" .. tostring(leader.PickingItem) .. ", bot=" .. tostring(bot.PickingItem))
                    local function drainTanks(inventory)
                        for item in inventory.AllItems do
                            if item.HasTag(Identifier("oxygensource")) then item.Condition = 0 end
                            if item.OwnInventory then drainTanks(item.OwnInventory) end
                        end
                    end
                    drainTanks(bot.Inventory)
                    bot.Oxygen = 15
                    after(500, function()
                        report("Low oxygen cancels active gathering",
                            started and bot.IsLowInOxygen and bot.PickingItem ~= other and other.ParentInventory == nil,
                            string.format("oxygen=%.1f, supply=%.1f, %s", bot.Oxygen, bot.OxygenAvailable, current()))
                        after(1400, function()
                            local tag = current()
                            report("Native survival objective takes over",
                                tag:lower():find("safety", 1, true) ~= nil, tag)
                            bot.SetOrder(order.GetDismissal(), true, false, true)
                        end)
                    end)
                end)
            end)
        end)
    end)
end

local function testCompetition()
    assert(second, "Need two crew bots")
    wearer(second, function()
        second.SetOrder(order, true, false, true)
        move(leader, base)
        move(bot, offset(base, -10, -70))
        move(second, offset(base, 10, -70))
        specimen("aquaticpoppy", offset(base, 0, -12), function(source)
            specimen("yeastshroom", offset(base, 0, -82), function(first)
                specimen("slimebacteria", offset(base, 24, -80), function(other)
                    after(1400, function()
                        move(leader, offset(source.WorldPosition, -15, 10))
                        move(bot, offset(first.WorldPosition, -8, 12))
                        move(second, offset(first.WorldPosition, 8, 12))
                        bot.Oxygen, second.Oxygen = 100, 100
                        source.TryInteract(leader, false, true, false)
                        after(2000, function()
                            local a, b = bot.PickingItem, second.PickingItem
                            report("Two bots simultaneously pick distinct plants",
                                (a == first and b == other) or (a == other and b == first),
                                tostring(a) .. " / " .. tostring(b) .. ", oxygen=" .. tostring(bot.Oxygen) ..
                                "/" .. tostring(second.Oxygen) .. ", second target distance=" ..
                                string.format("%.0f/%.0f", distance(bot, other), distance(second, other)))
                        end)
                        after(7900, function()
                            local a, b = first.ParentInventory, other.ParentInventory
                            report("Each bot keeps one plant",
                                (a == bot.Inventory and b == second.Inventory) or
                                (a == second.Inventory and b == bot.Inventory))
                            testOxygen()
                        end)
                    end)
                end)
            end)
        end)
    end)
end

wearer(leader, function()
    wearer(bot, function()
        if edgeCases then
            bot.SetOrder(order, true, false, true)
            testCompetition()
            return
        end
        spawn("divingknife", bot.Inventory, function(_)
            bot.SetOrder(order, true, false, true)
            move(leader, base)
            move(bot, offset(base, -560, 0))
            after(450, function()
                report("Crew moved outside", leader.CurrentHull == nil and bot.CurrentHull == nil, current())
                local before = distance(bot, leader)
                after(4500, function()
                    local afterDistance = distance(bot, leader)
                    report("Exterior follow closes distance", afterDistance < before - 50,
                        string.format("%.0f -> %.0f, %s", before, afterDistance, current()))
                    mining()
                end)
            end)
        end)
    end)
end)
