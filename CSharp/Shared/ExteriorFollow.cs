using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace EnhancedExperience
{
    internal static class ExteriorFollow
    {
        private const float ThreatRadius = 800f;
        private const float CombatLeash = 1050f;
        private const float ResourceRadius = 425f;
        private const float StartDistance = 450f;
        private const float StopDistance = 600f;
        private const float ActivityGrace = 2.5f;
        private const float ResourceRetry = 20f;
        private static readonly InvSlotType[] BothHands = { InvSlotType.LeftHand | InvSlotType.RightHand };
        private static readonly InvSlotType[] Hands = { InvSlotType.LeftHand, InvSlotType.RightHand };
        private static readonly ConditionalWeakTable<Character, Activity> Activities = new();
        private static readonly ConditionalWeakTable<Character, Escort> Escorts = new();
        private static readonly AccessTools.FieldRef<Pickable, Character> ActivePicker =
            AccessTools.FieldRefAccess<Pickable, Character>("activePicker");
        private static readonly Action<Pickable, Character> StopPicking =
            AccessTools.MethodDelegate<Action<Pickable, Character>>(AccessTools.Method(typeof(Pickable), "StopPicking"));
        private delegate bool FixBody(RepairTool tool, Character user, Vector2 hitPosition,
            float deltaTime, float degreeOfSuccess, Body targetBody);
        private static readonly FixBody CutBody = AccessTools.MethodDelegate<FixBody>(
            AccessTools.Method(typeof(RepairTool), "FixBody"));

        // Set only during the synchronous native cutter call made by the active escort.
        private static Escort cutting;

        public static void Install(Harmony harmony)
        {
            Patch(typeof(HumanAIController), nameof(HumanAIController.Update), prefix: nameof(BeforeAI));
            Patch(typeof(HumanAIController), "SpotEnemies", prefix: nameof(BeforeSpotEnemies));
            Patch(typeof(AIObjective), nameof(AIObjective.OnDeselected), postfix: nameof(AfterDeselected));
            Patch(typeof(AIObjectiveGoTo), "Act", prefix: nameof(BeforeGoTo));
            Patch(typeof(AIObjectiveGoTo), "get_CloseEnough", postfix: nameof(LimitFollowDistance));
            Patch(typeof(AIObjectiveCombat), "GetPriority", postfix: nameof(EscortCombatPriority));
            Patch(typeof(AIObjectiveFindSafety), "GetPriority", postfix: nameof(EscortSafetyPriority));
            Patch(typeof(Holdable), nameof(Holdable.Pick), prefix: nameof(BeforePick), postfix: nameof(AfterPick));
            Patch(typeof(RepairTool), "FixBody", prefix: nameof(BeforeFixBody), postfix: nameof(AfterFixBody));
            Patch(typeof(RepairTool), "Repair", prefix: nameof(RepairOnlyOre));

            void Patch(Type type, string method, string prefix = null, string postfix = null)
            {
                harmony.Patch(AccessTools.DeclaredMethod(type, method),
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(ExteriorFollow), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(ExteriorFollow), postfix));
            }
        }

        public static void Clear()
        {
            foreach (var entry in Escorts) { entry.Value.Stop(); }
            Escorts.Clear();
            Activities.Clear();
        }

        private static bool HasAuthority => GameMain.NetworkMember?.IsClient != true;
        private static double Now => Timing.TotalTime;

        private static bool Outside(Character character) =>
            character is { Removed: false, IsDead: false, CurrentHull: null, Submarine: null };

        private static bool IsEscort(AIObjectiveGoTo follow, Character bot) =>
            follow.IsFollowOrder && bot.IsBot && Outside(bot) &&
            follow.Target is Character { IsPlayer: true } leader && Outside(leader);

        private static bool Within(Entity a, Entity b, float distance) =>
            Vector2.DistanceSquared(a.WorldPosition, b.WorldPosition) <= distance * distance;

        private static void BeforeAI(HumanAIController __instance, float deltaTime)
        {
            if (!HasAuthority) { return; }
            Character bot = __instance.Character;
            AIObjectiveManager manager = __instance.ObjectiveManager;
            Escorts.TryGetValue(bot, out Escort escort);
            if (HumanAIController.DisableCrewAI || bot.IsIncapacitated ||
                manager.CurrentOrder is not AIObjectiveGoTo follow ||
                !IsEscort(follow, bot) || !manager.IsOrder(follow) || !follow.IsAllowed)
            {
                if (escort != null)
                {
                    escort.Stop();
                    Escorts.Remove(bot);
                }
                return;
            }

            if (escort?.Follow != follow)
            {
                escort?.Stop();
                Escorts.Remove(bot);
                escort = new Escort(bot, follow, __instance);
                Escorts.Add(bot, escort);
            }
            escort.Update(deltaTime);
        }

        private static bool BeforeSpotEnemies(HumanAIController __instance) =>
            !HasAuthority || !Escorts.TryGetValue(__instance.Character, out Escort escort) || !escort.HandlesThreats;

        private static void AfterDeselected(AIObjective __instance, Character ___character)
        {
            if (__instance is AIObjectiveGoTo { IsFollowOrder: true } &&
                Escorts.TryGetValue(___character, out Escort escort) && escort.Follow == __instance)
            {
                escort.Release();
            }
        }

        private static bool BeforeGoTo(AIObjectiveGoTo __instance, float deltaTime, Character ___character) =>
            !HasAuthority || !__instance.IsFollowOrder ||
            !Escorts.TryGetValue(___character, out Escort escort) || escort.Follow != __instance ||
            !escort.Collect(deltaTime);

        private static void LimitFollowDistance(AIObjectiveGoTo __instance, Character ___character, ref float __result)
        {
            if (__result > 400f && HasAuthority && IsEscort(__instance, ___character))
            {
                __result = 400f;
            }
        }

        private static void EscortCombatPriority(AIObjectiveCombat __instance, Character ___character, ref float __result)
        {
            if (HasAuthority && Escorts.TryGetValue(___character, out Escort escort))
            {
                __result = escort.CombatPriority(__instance, __result);
            }
        }

        private static void EscortSafetyPriority(Character ___character, ref float __result)
        {
            if (HasAuthority && Escorts.TryGetValue(___character, out Escort escort) &&
                escort.IsActive && escort.NeedsEmergencySafety)
            {
                __result = AIObjectiveManager.MaxObjectivePriority;
            }
        }

        private static bool IsPlant(Item item) =>
            item != null && item.HasTag("plant") && item.GetComponent<LevelResource>() != null;

        private static void BeforePick(Holdable __instance, Character picker, out bool __state) =>
            __state = HasAuthority && picker is { IsPlayer: true } && Outside(picker) &&
                __instance.Attached && IsPlant(__instance.Item);

        private static void AfterPick(Holdable __instance, Character picker, bool __state, bool __result)
        {
            if (__state && (__result || picker.PickingItem == __instance.Item))
            {
                Activities.GetOrCreateValue(picker).Record(__instance.Item, plants: true);
            }
        }

        private static void BeforeFixBody(RepairTool __instance, Character user, Body targetBody,
            out (Item Ore, float Progress) __state)
        {
            __state = default;
            if (HasAuthority && user is { IsPlayer: true } && Outside(user) &&
                __instance.Item.Prefab.Identifier.Value == "plasmacutter" &&
                targetBody?.UserData is Item ore && ore.HasTag("ore") &&
                ore.GetComponent<LevelResource>() is { Attached: true } resource)
            {
                __state = (ore, resource.DeattachTimer);
            }
        }

        private static void AfterFixBody(Character user, (Item Ore, float Progress) __state)
        {
            if (__state.Ore?.GetComponent<LevelResource>() is { } resource &&
                (!resource.Attached || resource.DeattachTimer > __state.Progress))
            {
                Activities.GetOrCreateValue(user).Record(__state.Ore, plants: false);
            }
        }

        private static bool RepairOnlyOre(RepairTool __instance, Character user, float deltaTime,
            float degreeOfSuccess, List<Body> ignoredBodies, ref Vector2 ___pickedPosition)
        {
            if (cutting == null || cutting.Bot != user) { return true; }
            if (cutting.CutApplied) { return false; }
            cutting.CutApplied = true;
            Item ore = cutting.Resource;
            if (ore?.GetComponent<LevelResource>() is not { Attached: true }) { return false; }

            Body hit = FindOreHit(user, ore, __instance.Range, ignoredBodies);
            if (hit?.UserData == ore)
            {
                ___pickedPosition = Submarine.LastPickedPosition;
                CutBody(__instance, user, ___pickedPosition, deltaTime, degreeOfSuccess, hit);
            }
            return false;
        }

        private static Body FindOreHit(Character bot, Item ore, float cutterRange, IEnumerable<Body> ignoredBodies = null)
        {
            Vector2 start = bot.AnimController.AimSourceSimPos;
            float range = ConvertUnits.ToSimUnits(cutterRange);
            if (Vector2.DistanceSquared(start, ore.SimPosition) > range * range) { return null; }
            return Submarine.PickBody(start, ore.SimPosition, ignoredBodies,
                Physics.CollisionWall | Physics.CollisionLevel | Physics.CollisionItemBlocking |
                Physics.CollisionItem | Physics.CollisionCharacter | Physics.CollisionRepairableWall,
                ignoreSensors: false,
                customPredicate: fixture => fixture.Body.UserData switch
                {
                    Limb limb when limb.character == bot => false,
                    Character character when character == bot => false,
                    Item item when item == ore => true,
                    Item item when bot.HeldItems.Contains(item) => false,
                    _ => !fixture.IsSensor
                },
                allowInsideFixture: true);
        }

        private sealed class Activity
        {
            public Item Item { get; private set; }
            public bool Plants { get; private set; }
            public double Time { get; private set; }

            public void Record(Item item, bool plants)
            {
                Item = item;
                Plants = plants;
                Time = Now;
            }
        }

        private sealed class Escort
        {
            public Character Bot { get; }
            public AIObjectiveGoTo Follow { get; }
            public Item Resource { get; private set; }
            public bool CutApplied { get; set; }

            private readonly HumanAIController ai;
            private readonly Character leader;
            private readonly Dictionary<Item, double> unavailable = new();
            private readonly Dictionary<Character, double> combatRetry = new();
            private readonly List<(Item Item, InvSlotType Slot)> stowed = new();
            private AIObjectiveManager Manager => ai.ObjectiveManager;
            private AIObjectiveCombat combat;
            private bool threatNearby;
            private double scanResources;
            private double giveUp;
            private double lastContact;
            private double activityUntil;
            private double ignoreActivityBefore;
            private double pickingStarted;
            private float threatTimer;
            private Item playerResource;
            private Item equippedCutter;
            private Item workingCutter;
            private bool harvestPlants;
            private bool handsPrepared;
            private bool emergencySafety;
            private AIObjectiveGoTo route;
            private Holdable picking;

            public Escort(Character bot, AIObjectiveGoTo follow, HumanAIController ai)
            {
                Bot = bot;
                Follow = follow;
                leader = (Character)follow.Target;
                this.ai = ai;
            }

            public bool IsActive => Outside(Bot) && Outside(leader) && !Bot.IsIncapacitated &&
                !HumanAIController.DisableCrewAI && Manager.CurrentOrder == Follow && Manager.IsOrder(Follow);

            public bool HandlesThreats => IsActive &&
                (Manager.CurrentObjective == Follow || Manager.CurrentObjective is AIObjectiveCombat);

            public bool NeedsEmergencySafety => (Bot.NeedsOxygen && Bot.Oxygen < 30 && Bot.OxygenAvailable <= 0) ||
                (Bot.InPressure && !Bot.IsProtectedFromPressure);

            private bool Critical => Bot.IsLowInOxygen || NeedsEmergencySafety ||
                Bot.Vitality < Bot.MaxVitality * 0.2f;

            public void Update(float deltaTime)
            {
                bool needsEmergencySafety = NeedsEmergencySafety;
                if (needsEmergencySafety != emergencySafety)
                {
                    emergencySafety = needsEmergencySafety;
                    ai.SortTimer = 0;
                }

                if (combat != null && (!Manager.Objectives.Contains(combat) || !combat.CanBeCompleted ||
                    combat.IsCompleted || !CanContinueCombat(combat.Enemy) || Now > lastContact + 2))
                {
                    if (!combat.Enemy.Removed && !combat.Enemy.IsDead) { combatRetry[combat.Enemy] = Now + 5; }
                    EndCombat();
                    threatTimer = 0;
                }

                if (Critical || (Manager.CurrentObjective != Follow && Manager.CurrentObjective is not AIObjectiveCombat))
                {
                    Release();
                    ignoreActivityBefore = Now;
                    return;
                }

                threatTimer -= deltaTime;
                if (threatTimer > 0) { return; }
                threatTimer = 0.2f;
                Character enemy = FindThreat();
                if (!threatNearby) { return; }
                if (enemy != null && combat?.Enemy == enemy) { lastContact = Now; }
                Release();
                activityUntil = 0;
                ignoreActivityBefore = Now;
                if (enemy == null || !Within(Bot, leader, CombatLeash) ||
                    Manager.Objectives.Any(objective => objective is AIObjectiveCombat && objective != combat &&
                        objective.CanBeCompleted && !objective.IsCompleted)) { return; }
                if (combat?.Enemy == enemy) { return; }
                EndCombat();
                lastContact = Now;
                combat = new AIObjectiveCombat(Bot, enemy, AIObjectiveCombat.CombatMode.Offensive,
                    Manager, priorityModifier: 0.95f)
                {
                    AbortCondition = _ => !CanContinueCombat(enemy)
                };
                Manager.AddObjective(combat);
                ai.SortTimer = 0;
            }

            private void EndCombat()
            {
                if (combat == null) { return; }
                if (combat.CanBeCompleted && !combat.IsCompleted && Manager.Objectives.Contains(combat))
                {
                    combat.Abandon = true;
                }
                combat = null;
                ai.SortTimer = 0;
            }

            public float CombatPriority(AIObjectiveCombat objective, float priority)
            {
                if (objective != combat) { return priority; }
                if (!CanContinueCombat(objective.Enemy)) { return 0; }
                // Vanilla discounts enemies attached to another character. Here that
                // character can be the escort target, whose attacker should still be engaged.
                if (priority == 0 && EnemyAIController.IsLatchedToSomeoneElse(objective.Enemy, Bot) &&
                    objective.Enemy.AIController?.SelectedAiTarget?.Entity == leader)
                {
                    return AIObjectiveManager.EmergencyObjectivePriority - 1;
                }
                return priority;
            }

            private bool Hostile(Character enemy, out bool attacking)
            {
                attacking = enemy != null && ((enemy.AIController?.SelectedAiTarget?.Entity is Character victim &&
                    (victim == leader || victim == Bot)) || leader.LastAttacker == enemy || Bot.LastAttacker == enemy);
                return enemy != Bot && enemy != leader && Outside(enemy) && !enemy.IsIncapacitated && !enemy.InDetectable &&
                    !ai.IsFriendly(enemy) && !HumanAIController.IsFriendly(leader, enemy) &&
                    (attacking || enemy.AIController is EnemyAIController { AttackHumans: true } ||
                    (enemy.IsHuman && enemy.TeamID is CharacterTeamType.Team1 or CharacterTeamType.Team2));
            }

            private bool CanContinueCombat(Character enemy) =>
                IsActive && !Critical && Hostile(enemy, out _) && Within(Bot, leader, CombatLeash) &&
                Within(enemy, leader, CombatLeash) &&
                (Within(enemy, Bot, ThreatRadius) || Within(enemy, leader, ThreatRadius));

            private Character FindThreat()
            {
                threatNearby = false;
                Character best = null;
                float bestScore = float.PositiveInfinity;
                foreach (Character enemy in Character.CharacterList)
                {
                    if (!Hostile(enemy, out bool attacking)) { continue; }
                    float distance = Math.Min(Vector2.DistanceSquared(enemy.WorldPosition, Bot.WorldPosition),
                        Vector2.DistanceSquared(enemy.WorldPosition, leader.WorldPosition));
                    if (distance > ThreatRadius * ThreatRadius ||
                        !Within(enemy, leader, CombatLeash) ||
                        (!Bot.CanSeeTarget(enemy) && !leader.CanSeeTarget(enemy))) { continue; }
                    threatNearby = true;
                    if (combatRetry.TryGetValue(enemy, out double retryUntil))
                    {
                        if (retryUntil > Now) { continue; }
                        combatRetry.Remove(enemy);
                    }

                    float score = distance - (attacking ? ThreatRadius * ThreatRadius : 0);
                    if (enemy == combat?.Enemy) { score -= 100 * 100; }
                    if (score >= bestScore) { continue; }
                    bestScore = score;
                    best = enemy;
                }
                return best;
            }

            public bool Collect(float deltaTime)
            {
                if (!IsActive || Manager.CurrentObjective != Follow || Critical || Bot.LockHands || !Bot.AnimController.InWater ||
                    !leader.AnimController.InWater || threatNearby || combat != null ||
                    !Within(Bot, leader, Resource == null ? StartDistance : StopDistance))
                {
                    Release();
                    return false;
                }

                if (leader.PickingItem is Item plant && IsPlant(plant))
                {
                    Observe(plant, plants: true, Now);
                }
                else if (Activities.TryGetValue(leader, out Activity activity) &&
                    activity.Time + ActivityGrace > Now && activity.Time >= ignoreActivityBefore &&
                    activity.Time >= activityUntil - ActivityGrace)
                {
                    Observe(activity.Item, activity.Plants, activity.Time);
                }

                if (Resource != null && (Resource.Removed || Resource.ParentInventory != null))
                {
                    Release();
                    return false;
                }

                if (Resource == null)
                {
                    if (Now >= activityUntil || Now < scanResources) { return false; }
                    scanResources = Now + 1;
                    if (!harvestPlants && FindCutter() == null &&
                        (FindCutter(allowEmpty: true) == null || SpareCutterFuel() == null)) { return false; }
                    Resource = FindResource();
                    if (Resource == null) { return false; }
                    giveUp = Now + 12;
                }

                Item item = Resource;
                if (Now >= giveUp || item == playerResource || !OutsideResource(item) ||
                    !Within(item, leader, ResourceRadius) || StorageSlot(item) < 0)
                {
                    Release(retry: true);
                    return false;
                }

                bool attached = item.GetComponent<LevelResource>().Attached;
                if ((attached && !harvestPlants && !Within(Bot, item, 105)) ||
                    ((harvestPlants || !attached) && !Bot.CanInteractWith(item, checkLinked: false)))
                {
                    if (picking != null) { Release(retry: true); return false; }
                    route ??= new AIObjectiveGoTo(item, Bot, Manager, repeat: true, getDivingGearIfNeeded: false)
                    {
                        AllowGoingOutside = true,
                        CloseEnough = 50,
                        SpeakIfFails = false,
                        DebugLogWhenFails = false,
                        FaceTargetOnCompleted = false,
                        UseDistanceRelativeToAimSourcePos = true
                    };
                    route.TryComplete(deltaTime);
                    if (!route.CanBeCompleted) { Release(retry: true); return false; }
                    return true;
                }

                ai.SteeringManager.Reset();
                ai.FaceTarget(item);
                if (!attached)
                {
                    int slot = StorageSlot(item);
                    if (slot >= 0) { Bot.Inventory.TryPutItem(item, slot, false, true, Bot); }
                    Release(retry: item.ParentInventory != Bot.Inventory);
                    return true;
                }

                if (!handsPrepared)
                {
                    workingCutter = harvestPlants ? null : LoadCutter();
                }
                if ((!harvestPlants && (workingCutter == null || !IsCutter(workingCutter) || !HasCutterFuel(workingCutter))) ||
                    (!handsPrepared && !PrepareHands(workingCutter, harvestPlants)))
                {
                    Release(retry: true);
                    return false;
                }

                if (harvestPlants)
                {
                    var holdable = item.GetComponent<Holdable>();
                    if (holdable == null || (ActivePicker(holdable) != null && ActivePicker(holdable) != Bot))
                    {
                        Release(retry: true);
                        return false;
                    }
                    if (picking == null)
                    {
                        picking = holdable;
                        pickingStarted = Now;
                        item.TryInteract(Bot, forceSelectKey: holdable.PickKey == InputType.Select,
                            forceUseKey: holdable.PickKey == InputType.Use);
                    }
                    else if (Bot.PickingItem != item && Now > pickingStarted + 0.25)
                    {
                        Release(retry: true);
                        return false;
                    }
                }
                else
                {
                    if (!Bot.HasEquippedItem(workingCutter)) { Release(retry: true); return false; }
                    float range = workingCutter.GetComponent<RepairTool>().Range;
                    if (FindOreHit(Bot, item, range)?.UserData != item)
                    {
                        Release(retry: true);
                        return false;
                    }
                    Bot.CursorPosition = item.Position;
                    Bot.SetInput(InputType.Aim, false, true);
                    cutting = this;
                    CutApplied = false;
                    try { workingCutter.Use(deltaTime, Bot); }
                    finally { cutting = null; }
                }
                return true;
            }

            private void Observe(Item item, bool plants, double when)
            {
                if (harvestPlants != plants) { Release(); }
                harvestPlants = plants;
                playerResource = item;
                activityUntil = when + ActivityGrace;
            }

            private static bool OutsideResource(Item item) =>
                !item.Removed && item.Submarine == null && item.CurrentHull == null &&
                item.ParentInventory == null && item.Condition > 0 &&
                item.GetComponent<LevelResource>() != null;

            private Item FindResource()
            {
                foreach (Item item in unavailable.Where(entry => entry.Value <= Now || entry.Key.Removed)
                    .Select(entry => entry.Key).ToArray())
                {
                    unavailable.Remove(item);
                }
                var reserved = Escorts.Where(entry => entry.Value != this && entry.Value.Resource != null &&
                    entry.Value.IsActive).Select(entry => entry.Value.Resource).ToHashSet();
                Item best = null;
                float bestDistance = float.PositiveInfinity;
                foreach (Item item in Item.ItemList)
                {
                    if (item == playerResource || !Within(item, leader, ResourceRadius) ||
                        !OutsideResource(item) || !item.HasTag(harvestPlants ? "plant" : "ore") ||
                        reserved.Contains(item) || unavailable.ContainsKey(item) ||
                        !item.IsInteractable(Bot) || StorageSlot(item) < 0) { continue; }

                    if (!item.GetComponent<LevelResource>().Attached ||
                        (harvestPlants && item.GetComponent<Holdable>() is { } holdable && ActivePicker(holdable) != null))
                    {
                        continue;
                    }
                    float distance = Vector2.DistanceSquared(item.WorldPosition, Bot.WorldPosition);
                    if (distance >= bestDistance) { continue; }
                    bestDistance = distance;
                    best = item;
                }
                return best;
            }

            private Item FindCutter(bool allowEmpty = false) => Bot.Inventory?.FindItem(
                item => IsCutter(item) && (allowEmpty || HasCutterFuel(item)), recursive: true);

            private bool IsCutter(Item item) =>
                !item.Removed && item.Prefab.Identifier.Value == "plasmacutter" && item.Condition > 0 &&
                item.IsInteractable(Bot) && item.GetComponent<RepairTool>() != null;

            private static bool HasCutterFuel(Item item) =>
                item.ContainedItems.Any(tank => tank.Condition > 0 && tank.HasTag("oxygensource"));

            private Item SpareCutterFuel()
            {
                // Reserve two usable tanks for breathing. Only take a loose, ordinary
                // oxygen tank and leave tanks already installed in equipment alone.
                var reserve = Bot.Inventory.AllItems.Where(item => item.HasTag("oxygensource") &&
                    !item.Removed && item.ConditionPercentage >= 10 && item.IsInteractable(Bot)).Distinct().ToArray();
                return reserve.Length <= 2 ? null :
                    reserve.FirstOrDefault(item => item.Prefab.Identifier.Value == "oxygentank");
            }

            private Item LoadCutter()
            {
                Item loaded = FindCutter();
                if (loaded != null) { return loaded; }
                Item cutter = FindCutter(allowEmpty: true);
                if (cutter?.OwnInventory is not ItemInventory fuelInventory || SpareCutterFuel() is not Item fuel)
                {
                    return null;
                }
                Item old = cutter.ContainedItems.FirstOrDefault();
                if (old != null)
                {
                    int slot = StorageSlot(old);
                    if (slot < 0 || !Bot.Inventory.TryPutItem(old, slot, false, false, Bot)) { return null; }
                }
                return fuelInventory.TryPutItem(fuel, Bot) ? cutter : null;
            }

            private int StorageSlot(Item item)
            {
                CharacterInventory inventory = Bot.Inventory;
                if (inventory == null) { return -1; }
                for (int index = 0; index < inventory.Capacity; index++)
                {
                    if (inventory.SlotTypes[index] == InvSlotType.Any && inventory.CanBePutInSlot(item, index))
                    {
                        return index;
                    }
                }
                return -1;
            }

            private bool PrepareHands(Item cutter, bool plants)
            {
                CharacterInventory inventory = Bot.Inventory;
                foreach (InvSlotType slot in Hands)
                {
                    Item held = inventory.GetItemInLimbSlot(slot);
                    if (held == null)
                    {
                        if (plants) { break; }
                        continue;
                    }
                    if (held == cutter) { continue; }
                    int storage = StorageSlot(held);
                    if (storage < 0 || !inventory.TryPutItem(held, storage, false, false, Bot)) { continue; }
                    stowed.Add((held, slot));
                    if (plants) { break; }
                }

                if (plants)
                {
                    handsPrepared = Hands.Any(slot => inventory.GetItemInLimbSlot(slot) == null);
                    return handsPrepared;
                }

                if (Hands.Any(slot => inventory.GetItemInLimbSlot(slot) is Item held && held != cutter)) { return false; }
                if (!Bot.HasEquippedItem(cutter))
                {
                    if (!inventory.TryPutItem(cutter, Bot, BothHands, createNetworkEvent: true)) { return false; }
                    equippedCutter = cutter;
                }
                handsPrepared = true;
                return true;
            }

            public void Release(bool retry = false)
            {
                if (Resource == null) { return; }
                if (retry && !Resource.Removed) { unavailable[Resource] = Now + ResourceRetry; }
                if (picking != null && ActivePicker(picking) == Bot) { StopPicking(picking, Bot); }
                if (!Bot.Removed && !Bot.IsDead && Bot.Inventory != null)
                {
                    if (equippedCutter != null && Bot.HasEquippedItem(equippedCutter))
                    {
                        int slot = StorageSlot(equippedCutter);
                        if (slot >= 0) { Bot.Inventory.TryPutItem(equippedCutter, slot, false, false, Bot); }
                    }
                    foreach (var (item, slot) in stowed)
                    {
                        InvSlotType required = item.AllowedSlots.Contains(slot) ? slot : BothHands[0];
                        if (!item.Removed && item.ParentInventory == Bot.Inventory && Hands.All(hand =>
                            (required & hand) == 0 || Bot.Inventory.GetItemInLimbSlot(hand) == null ||
                            Bot.Inventory.GetItemInLimbSlot(hand) == item))
                        {
                            int index = Array.IndexOf(Bot.Inventory.SlotTypes, slot);
                            if (index >= 0) { Bot.Inventory.TryPutItem(item, index, false, false, Bot); }
                        }
                    }
                }
                stowed.Clear();
                equippedCutter = null;
                workingCutter = null;
                handsPrepared = false;
                picking = null;
                route = null;
                Resource = null;
            }

            public void Stop()
            {
                Release();
                EndCombat();
            }
        }
    }
}
