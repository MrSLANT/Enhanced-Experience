using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    internal sealed class SecurityDefense : AIObjective
    {
        private static readonly Identifier Periscope = "periscope".ToIdentifier();
        private static readonly ImmutableArray<Identifier> PeriscopeTags = ImmutableArray.Create(Periscope);
        private static ConditionalWeakTable<Submarine, DefensePlan> plans = new();
        private static readonly Func<Turret, List<Projectile>> LoadedProjectiles =
            AccessTools.Method(typeof(Turret), "GetLoadedProjectiles").CreateDelegate<Func<Turret, List<Projectile>>>();
        private static readonly Func<Turret, Vector2, Vector2, Body> TraceShot =
            AccessTools.Method(typeof(Turret), "CheckLineOfSight").CreateDelegate<Func<Turret, Vector2, Vector2, Body>>();

        private readonly Dictionary<Turret, double> retryAfter = new();
        private WeaponOperation operation;

        private sealed record Station(Turret Turret, Controller Controller, bool Ready);
        private sealed record Assignment(Station Station, ISpatialEntity Target, float Score);

        private sealed class DefensePlan
        {
            internal readonly Dictionary<Character, Assignment> Weapons = new();
            internal readonly Dictionary<Character, Character> Intruders = new();
            internal readonly HashSet<Submarine> Aggressors = new();
            internal readonly Dictionary<Character, double> RecentAttackers = new();
            internal double RefreshAfter;
        }

        public override Identifier Identifier { get; set; } = "operateweapons".ToIdentifier();
        public override bool AbandonWhenCannotCompleteSubObjectives => false;

        private SecurityDefense(Character officer, AIObjectiveManager manager)
            : base(officer, manager, 1f, Identifier.Empty) { IgnoreAtOutpost = true; }

        internal static void InstallPatches(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Turret), nameof(Turret.CrewAIOperate)),
                prefix: new HarmonyMethod(typeof(SecurityDefense), nameof(SelectNativeTurretTarget)),
                postfix: new HarmonyMethod(typeof(SecurityDefense), nameof(ReleaseTurretTarget)));
            harmony.Patch(AccessTools.Method(typeof(Turret), "TryLaunch"),
                prefix: new HarmonyMethod(typeof(SecurityDefense), nameof(CheckAutomaticLaunch)));
            harmony.Patch(AccessTools.Method(typeof(HumanAIController), "SpotEnemies"),
                prefix: new HarmonyMethod(typeof(SecurityDefense), nameof(KeepDefenseAssignment)));
            harmony.Patch(AccessTools.Method(typeof(Structure), nameof(Structure.AddDamage),
                    new[] { typeof(int), typeof(float), typeof(Character), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(SecurityDefense), nameof(BeforeHullDamage)),
                postfix: new HarmonyMethod(typeof(SecurityDefense), nameof(AfterHullDamage)));
        }

        internal static void Clear() => plans = new();

        internal static void InstallForCrew(AIObjectiveManager manager, Character officer)
        {
            if (!CrewRepairPriorities.AppliesTo(officer) ||
                officer.Info?.Job?.Prefab.Identifier != "securityofficer") { return; }

            bool Replaced(AIObjective objective) =>
                objective is AIObjectiveFightIntruders and not IntruderDefense ||
                objective is AIObjectiveOperateItem && objective.Identifier == "operateweapons";
            manager.Objectives.RemoveAll(Replaced);
            foreach (var pending in manager.DelayedObjectives.Where(entry => Replaced(entry.Key)).ToArray())
            {
                CoroutineManager.StopCoroutines(pending.Value);
                manager.DelayedObjectives.Remove(pending.Key);
            }
            if (manager.GetObjective<SecurityDefense>() == null)
            {
                manager.AddObjective(new SecurityDefense(officer, manager));
                manager.AddObjective(new IntruderDefense(officer, manager));
            }
        }

        private static bool HasOrders(AIObjectiveManager manager) =>
            manager.ForcedOrder != null || manager.CurrentOrder?.Priority > 0f;

        private static bool Eligible(Character officer) =>
            !officer.IsPlayer && !officer.IsIncapacitated && !officer.LockHands &&
            CrewRepairPriorities.AppliesTo(officer) && officer.Info?.Job?.Prefab.Identifier == "securityofficer";

        private static DefensePlan PlanFor(Submarine submarine)
        {
            var plan = plans.GetOrCreateValue(submarine);
            double now = Timing.TotalTimeUnpaused;
            if (now >= plan.RefreshAfter)
            {
                plan.RefreshAfter = now + 1.0;
                Assign(submarine, plan, now);
            }
            return plan;
        }

        internal static int IntruderSlots(int officers, int intruders, bool externalThreat) =>
            externalThreat && officers > 1
                ? Math.Min(intruders, officers - 1)
                : Math.Min(intruders, officers);

        internal static bool IsRailgun(Identifier prefab, Identifier variantOf) =>
            prefab == "railgun" || prefab == "legacyrailgun" || prefab == "railgunwrecked" || variantOf == "railgun";

        private static void Assign(Submarine submarine, DefensePlan plan, double now)
        {
            var previousWeapons = plan.Weapons.ToArray();
            var previousIntruders = plan.Intruders.ToArray();
            plan.Weapons.Clear();
            plan.Intruders.Clear();
            plan.Aggressors.RemoveWhere(ship => ship.Removed);
            foreach (var attacker in plan.RecentAttackers.Where(pair => pair.Key.Removed || pair.Value <= now).ToArray())
            {
                plan.RecentAttackers.Remove(attacker.Key);
            }

            var characters = Character.CharacterList;
            var officers = characters.Where(officer => officer.Submarine == submarine && Eligible(officer) &&
                officer.AIController is HumanAIController ai && !HasOrders(ai.ObjectiveManager) &&
                ai.ObjectiveManager.CurrentObjective is not AIObjectiveFindSafety { Priority: > 60f } &&
                ai.ObjectiveManager.GetObjective<SecurityDefense>() != null).OrderBy(officer => officer.ID).ToList();
            if (officers.Count == 0 || submarine.Info.IsOutpost) { return; }

            var observers = officers.Select(officer => ((HumanAIController)officer.AIController).ObjectiveManager)
                .Select(manager => manager.GetObjective<IntruderDefense>()).Where(objective => objective != null).ToArray();
            var intruders = characters.Where(target => target.CurrentHull?.Submarine == submarine &&
                officers.Any(officer => AIObjectiveFightIntruders.IsValidTarget(target, officer, false)) &&
                (observers.Any(objective => objective.ReportedTargets.Contains(target)) ||
                 officers.Any(officer => officer.CanSeeTarget(target)) ||
                 previousIntruders.Any(previous => previous.Value == target))).ToList();

            var threats = characters.Where(target => IsExternalThreat(officers[0], submarine, target, plan))
                .Cast<ISpatialEntity>().ToList();
            if (plan.Aggressors.Count > 0)
            {
                threats.AddRange(Structure.WallList.Where(wall => IsIntactHull(wall) &&
                    wall.Submarine != null && plan.Aggressors.Contains(wall.Submarine) &&
                    !submarine.IsConnectedTo(wall.Submarine) && wall.Submarine.TeamID != submarine.TeamID));
            }

            var candidates = new List<(Character Officer, Assignment Assignment)>();
            if (threats.Count > 0)
            {
                var stations = Item.ItemList.Where(item => item.Submarine == submarine && !item.Removed &&
                        item.Condition > 0f && !IsRailgun(item.Prefab.Identifier, item.Prefab.VariantOf))
                    .Select(item => item.GetComponent<Turret>()).Where(turret => turret != null)
                    .Select(turret => (Turret: turret, Controller: turret.Item.FindController(PeriscopeTags)))
                    .Where(pair => pair.Controller?.Item is { Removed: false, Condition: > 0f } controller &&
                        controller.HasTag(Periscope) && controller.Submarine == submarine)
                    .Select(pair => new Station(pair.Turret, pair.Controller, pair.Turret.HasPowerToShoot() &&
                        (pair.Turret.LaunchWithoutProjectile || LoadedProjectiles(pair.Turret).Count > 0))).ToArray();

                foreach (var station in stations)
                {
                    var visibleTargets = threats.Where(target => CanEngage(station.Turret, target, officers[0])).ToArray();
                    foreach (var officer in officers)
                    {
                        var defense = ((HumanAIController)officer.AIController).ObjectiveManager.GetObjective<SecurityDefense>();
                        if (!station.Controller.Item.HasAccess(officer) || StationOccupied(station, officer) ||
                            defense.retryAfter.TryGetValue(station.Turret, out double retry) && retry > now) { continue; }

                        foreach (var target in visibleTargets)
                        {
                            bool committed = previousWeapons.Any(pair => pair.Key == officer &&
                                pair.Value.Station.Turret == station.Turret && pair.Value.Target == target);
                            float score = ScoreTarget(submarine, plan, station, target) +
                                (committed ? 30f : 0f) -
                                Math.Min(Vector2.Distance(officer.WorldPosition, station.Controller.Item.WorldPosition) / 200f, 20f);
                            candidates.Add((officer, new Assignment(station, target, score)));
                        }
                    }
                }
            }

            var usedStations = new HashSet<Controller>();
            var usedTurrets = new HashSet<Turret>();
            var coveredTargets = new HashSet<ISpatialEntity>();
            bool AssignWeapon()
            {
                var choice = candidates.Where(candidate => officers.Contains(candidate.Officer) &&
                        !usedStations.Contains(candidate.Assignment.Station.Controller) &&
                        !usedTurrets.Contains(candidate.Assignment.Station.Turret))
                    .OrderByDescending(candidate => candidate.Assignment.Score -
                        (coveredTargets.Contains(candidate.Assignment.Target.Submarine is { } enemyShip &&
                            candidate.Assignment.Target is Structure ? enemyShip : candidate.Assignment.Target) ? 25f : 0f))
                    .ThenBy(candidate => candidate.Assignment.Station.Turret.Item.ID).FirstOrDefault();
                if (choice.Officer == null) { return false; }
                var assignment = choice.Assignment;
                plan.Weapons.Add(choice.Officer, assignment);
                officers.Remove(choice.Officer);
                usedStations.Add(assignment.Station.Controller);
                usedTurrets.Add(assignment.Station.Turret);
                coveredTargets.Add(assignment.Target is Structure ? assignment.Target.Submarine : assignment.Target);
                return true;
            }

            bool canCoverOutside = candidates.Count > 0;
            int insideSlots = IntruderSlots(officers.Count, intruders.Count, canCoverOutside);
            if (officers.Count > 1 && intruders.Count > 0 && canCoverOutside) { AssignWeapon(); }
            for (int i = 0; i < insideSlots; i++)
            {
                var choice = (from intruder in intruders
                              from officer in officers
                              where AIObjectiveFightIntruders.IsValidTarget(intruder, officer, false)
                              where ((HumanAIController)officer.AIController).ObjectiveManager
                                  .GetObjective<IntruderDefense>()?.CanHandle(intruder) == true
                              orderby Vector2.DistanceSquared(officer.WorldPosition, intruder.WorldPosition) *
                                  (previousIntruders.Any(pair => pair.Key == officer && pair.Value == intruder) ? 0.5f : 1f)
                              select (officer, intruder)).FirstOrDefault();
                if (choice.officer == null) { break; }
                plan.Intruders.Add(choice.officer, choice.intruder);
                officers.Remove(choice.officer);
                intruders.Remove(choice.intruder);
            }
            while (AssignWeapon()) { }
        }

        private static bool StationOccupied(Station station, Character officer) =>
            Character.CharacterList.Any(other => other != officer && !other.IsDead &&
                (other.SelectedItem == station.Controller.Item ||
                 other.AIController is HumanAIController ai &&
                 (ai.ObjectiveManager.ForcedOrder ?? ai.ObjectiveManager.CurrentOrder) is AIObjectiveOperateItem order &&
                 order.GetTarget() == station.Controller));

        private static bool IsIntactHull(Structure wall) =>
            !wall.Removed && wall.HasBody && !wall.Indestructible && !wall.AllSectionBodiesDisabled();

        private static bool IsExternalThreat(Character officer, Submarine submarine, Character target, DefensePlan plan) =>
            target.Enabled && !target.Removed && !target.IsDead && !target.InDetectable && !target.LockHands &&
            target.AITurretPriority > 0f && target.CurrentHull == null &&
            !HumanAIController.IsFriendly(officer, target, false, true) &&
            (target.Submarine == null || target.Submarine != submarine && !target.Submarine.IsConnectedTo(submarine)) &&
            (!target.IsHuman || plan.RecentAttackers.ContainsKey(target) ||
                target.Submarine != null && plan.Aggressors.Contains(target.Submarine));

        private static float ScoreTarget(Submarine submarine, DefensePlan plan, Station station, ISpatialEntity target)
        {
            float threat = target is Structure ? 100f : 0f;
            if (target is Character enemy)
            {
                var attacked = enemy.AIController?.SelectedAiTarget?.Entity;
                threat = Math.Min(enemy.AITurretPriority * 10f, 25f);
                if (plan.RecentAttackers.ContainsKey(enemy) || attacked == submarine || attacked?.Submarine == submarine)
                {
                    threat += 100f;
                }
            }
            return threat + (station.Ready ? 45f : 0f) -
                Math.Min(Vector2.Distance(station.Turret.Item.WorldPosition, target.WorldPosition) / 100f, 40f);
        }

        private static bool CanEngage(Turret turret, ISpatialEntity target, Character officer)
        {
            Vector2 origin = turret.Item.WorldPosition;
            Vector2 point = target.WorldPosition;
            float range = Math.Min(turret.AIRange * turret.Item.OffsetOnSelectedMultiplier, 10000f);
            if (Vector2.DistanceSquared(origin, point) > range * range ||
                !turret.IsWithinAimingRadius(point)) { return false; }

            var body = TraceShot(turret, ConvertUnits.ToSimUnits(origin), ConvertUnits.ToSimUnits(point));
            if (body?.UserData is Limb limb &&
                (target != limb.character && HumanAIController.IsFriendly(officer, limb.character, false, true))) { return false; }
            if (body?.UserData is Character blocker &&
                (target != blocker && HumanAIController.IsFriendly(officer, blocker, false, true))) { return false; }
            if (body?.UserData is ISpatialEntity hit)
            {
                var hitSub = hit as Submarine ?? hit.Submarine;
                if (hitSub != null && (target is not Structure || hitSub != target.Submarine)) { return false; }
            }
            if (body != null && body.UserData is not ISpatialEntity && body.UserData is not Limb) { return false; }

            // Keep flak bursts away from friendly swimmers and occupied compartments.
            if (turret.Item.Prefab.Identifier == "flakcannon" || turret.Item.Prefab.VariantOf == "flakcannon")
            {
                const float clearance = 500f;
                if (Character.CharacterList.Any(other => !other.IsDead && other.CurrentHull == null &&
                        Vector2.DistanceSquared(other.WorldPosition, point) < clearance * clearance &&
                        HumanAIController.IsFriendly(officer, other, false, true))) { return false; }
                if (Hull.HullList.Any(hull => hull.Submarine != null &&
                    (hull.Submarine.TeamID == officer.Submarine.TeamID || hull.Submarine.IsConnectedTo(officer.Submarine)) &&
                    NearHull(hull.WorldRect, point, clearance))) { return false; }
            }
            return true;
        }

        private static bool NearHull(Rectangle bounds, Vector2 point, float clearance)
        {
            float x = MathHelper.Clamp(point.X, bounds.X, bounds.Right);
            float y = MathHelper.Clamp(point.Y, bounds.Y - bounds.Height, bounds.Y);
            return Vector2.DistanceSquared(point, new Vector2(x, y)) < clearance * clearance;
        }

        protected override bool CheckObjectiveState() => false;
        protected override void Act(float deltaTime) { }

        protected override float GetPriority()
        {
            if (operation is { } previous && (previous.Abandon || previous.IsCompleted))
            {
                retryAfter[operation.Turret] = Timing.TotalTimeUnpaused + 15.0;
                if (character.Submarine is { } sub) { plans.GetOrCreateValue(sub).RefreshAfter = 0; }
                StopOperation();
            }
            if (!IsAllowed || !Eligible(character) || HasOrders(objectiveManager) || character.Submarine == null ||
                !PlanFor(character.Submarine).Weapons.TryGetValue(character, out var assignment))
            {
                StopOperation();
                return 0f;
            }
            if (operation?.Turret != assignment.Station.Turret)
            {
                StopOperation();
                operation = new WeaponOperation(character, objectiveManager, assignment);
                AddSubObjective(operation);
            }
            operation.Target = assignment.Target;
            return AIObjectiveManager.LowestOrderPriority - 0.125f;
        }

        private void StopOperation()
        {
            if (operation == null) { return; }
            if (character.SelectedItem == operation.Controller.Item) { character.SelectedItem = null; }
            RemoveSubObjective(ref operation);
        }

        public override void OnDeselected()
        {
            if (operation != null && character.SelectedItem == operation.Controller.Item) { character.SelectedItem = null; }
            base.OnDeselected();
        }

        public override void OnSelected()
        {
            // Vanilla resets the selected root and clears its subobjectives.
            base.OnSelected();
            if (operation == null) { return; }
            operation.Reset();
            AddSubObjective(operation);
        }

        private sealed class WeaponOperation : AIObjectiveOperateItem
        {
            internal readonly Turret Turret;
            internal readonly Controller Controller;
            internal ISpatialEntity Target;

            internal WeaponOperation(Character officer, AIObjectiveManager manager, Assignment assignment)
                : base(assignment.Station.Turret, officer, manager, Identifier.Empty, false, null, true,
                    assignment.Station.Controller, 1f)
            {
                Turret = assignment.Station.Turret;
                Controller = assignment.Station.Controller;
                Target = assignment.Target;
                Identifier = "operateweapons".ToIdentifier();
            }
        }

        private sealed class IntruderDefense : AIObjectiveFightIntruders
        {
            internal IntruderDefense(Character officer, AIObjectiveManager manager) : base(officer, manager)
            {
                Identifier = "fightintruders".ToIdentifier();
            }

            internal bool CanHandle(Character target) => !ignoreList.Contains(target);

            protected override void FindTargets()
            {
                if (character.Submarine is { } submarine &&
                    PlanFor(submarine).Intruders.TryGetValue(character, out var intruder) &&
                    !ignoreList.Contains(intruder) && IsValidTarget(intruder))
                {
                    Targets.Add(intruder);
                }
            }

            protected override float GetPriority() =>
                IsAllowed && !HasOrders(objectiveManager) &&
                character.Submarine is { } submarine &&
                PlanFor(submarine).Intruders.TryGetValue(character, out var intruder) &&
                Targets.Contains(intruder) && !ignoreList.Contains(intruder) && IsValidTarget(intruder)
                    ? AIObjectiveManager.LowestOrderPriority - 0.25f
                    : 0f;
        }

        private static bool KeepDefenseAssignment(HumanAIController __instance)
        {
            var officer = __instance.Character;
            if (!Eligible(officer) || officer.Submarine is not { } submarine ||
                __instance.ObjectiveManager.GetObjective<SecurityDefense>() == null) { return true; }
            if (HasOrders(__instance.ObjectiveManager)) { return false; }
            var plan = PlanFor(submarine);
            return !plan.Weapons.ContainsKey(officer) && !plan.Intruders.ContainsKey(officer);
        }

        private static bool SelectNativeTurretTarget(Turret __instance, Character character, AIObjectiveOperateItem objective,
            ref ISpatialEntity ___currentTarget, ref float ___aiFindTargetTimer,
            out (bool Active, ISpatialEntity Target, float Timer) __state)
        {
            __state = default;
            if (objective is not WeaponOperation operation) { return true; }
            if (operation.Turret != __instance || character.Submarine is not { } submarine || !Eligible(character) ||
                character.AIController is not HumanAIController ai || HasOrders(ai.ObjectiveManager)) { return false; }

            if (!IsAssignedTargetValid(operation, character, submarine)) { return false; }

            __state = (true, ___currentTarget, ___aiFindTargetTimer);
            ___currentTarget = operation.Target;
            ___aiFindTargetTimer = 1f;
            return true;
        }

        private static bool IsAssignedTargetValid(WeaponOperation operation, Character character, Submarine submarine)
        {
            var plan = plans.GetOrCreateValue(submarine);
            bool hostile = operation.Target switch
            {
                Character enemy => IsExternalThreat(character, submarine, enemy, plan),
                Structure wall => IsIntactHull(wall) && wall.Submarine != null && plan.Aggressors.Contains(wall.Submarine) &&
                    wall.Submarine.TeamID != submarine.TeamID && !submarine.IsConnectedTo(wall.Submarine),
                _ => false
            };
            return hostile && CanEngage(operation.Turret, operation.Target, character);
        }

        private static void ReleaseTurretTarget((bool Active, ISpatialEntity Target, float Timer) __state,
            ref ISpatialEntity ___currentTarget, ref float ___aiFindTargetTimer)
        {
            if (!__state.Active) { return; }
            ___currentTarget = __state.Target;
            ___aiFindTargetTimer = __state.Timer;
        }

        // A custom periscope may send its trigger signal to several weapons at once.
        private static bool CheckAutomaticLaunch(Turret __instance, Character character)
        {
            if (character == null || character.IsPlayer || GameMain.NetworkMember?.IsClient == true ||
                character.AIController is not HumanAIController ai ||
                ai.ObjectiveManager.CurrentObjective is not SecurityDefense defense ||
                defense.operation is not { } operation || character.SelectedItem != operation.Controller.Item) { return true; }

            return !HasOrders(ai.ObjectiveManager) && operation.Turret == __instance &&
                character.Submarine is { } submarine && IsAssignedTargetValid(operation, character, submarine);
        }

        private static void BeforeHullDamage(Structure __instance, int sectionIndex, float damage, Character attacker, out float __state)
        {
            __state = GameMain.NetworkMember?.IsClient != true && attacker != null && damage > 0f &&
                __instance.Submarine != null && sectionIndex >= 0 && sectionIndex < __instance.SectionCount
                ? __instance.SectionDamage(sectionIndex) : -1f;
        }

        private static void AfterHullDamage(Structure __instance, int sectionIndex, Character attacker, float __state)
        {
            if (__state < 0f || __instance.Submarine is not { } damaged ||
                __instance.SectionDamage(sectionIndex) <= __state) { return; }

            var plan = plans.GetOrCreateValue(damaged);
            plan.RecentAttackers[attacker] = Timing.TotalTimeUnpaused + 20.0;
            if (attacker.Submarine is { } attacking && attacking != damaged &&
                attacking.TeamID != damaged.TeamID && !damaged.IsConnectedTo(attacking) &&
                !attacking.Info.IsOutpost && !attacking.Info.IsWreck)
            {
                plan.Aggressors.Add(attacking);
            }
        }
    }
}
