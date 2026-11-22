using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace EnhancedExperience
{
    internal static class CrewRepairPriorities
    {
        private const int InspectionUrgency = -1;
        private const float InspectionPriority = 25f;
        private const double InspectionInterval = 90d;
        private static readonly Identifier Mechanical = "mechanical".ToIdentifier();
        private static readonly Identifier Electrical = "electrical".ToIdentifier();
        private static ConditionalWeakTable<Submarine, RepairPlan> plans = new();

        private sealed class RepairPlan
        {
            internal double RefreshAfter;
            internal readonly Dictionary<Hull, double> InspectedAt = new();
            internal Dictionary<Character, Bid<Character, MapEntity>> Assignments = new();
        }

        internal sealed record Bid<TWorker, TTarget>(TWorker Worker, TTarget Target, int Urgency, float Score);

        internal static Dictionary<TWorker, Bid<TWorker, TTarget>> Assign<TWorker, TTarget>(
            IEnumerable<Bid<TWorker, TTarget>> candidates)
        {
            var assignments = new Dictionary<TWorker, Bid<TWorker, TTarget>>();
            var targets = new HashSet<TTarget>();
            foreach (var bid in candidates.OrderByDescending(bid => bid.Urgency).ThenByDescending(bid => bid.Score))
            {
                if (!assignments.ContainsKey(bid.Worker) && targets.Add(bid.Target))
                {
                    assignments.Add(bid.Worker, bid);
                }
            }
            return assignments;
        }

        internal static void Clear() => plans = new();

        internal static bool AppliesTo(Character crew) =>
            GameMain.NetworkMember?.IsClient != true && crew.IsBot && crew.IsOnPlayerTeam && !crew.IsDead;

        internal static bool Available(Character crew) =>
            AppliesTo(crew) && HumanAIController.IsActive(crew) && !crew.IsPlayer && !crew.Removed &&
            !crew.IsIncapacitated && !crew.LockHands && crew.CurrentHull is { Submarine.Info.IsOutpost: false };

        internal static bool HasOrders(AIObjectiveManager manager) =>
            manager.ForcedOrder != null || manager.CurrentOrders.Any(order => order.Objective is
                { Abandon: false, IsCompleted: false, Priority: > 0f });

        internal static bool CanTakeDuty(AIObjectiveManager manager, bool emergency) =>
            manager.ForcedOrder == null &&
            manager.CurrentObjective is not AIObjectiveFindSafety { Priority: > AIObjectiveManager.HighestOrderPriority } &&
            (emergency
                ? !manager.CurrentOrders.Any(order => order.Objective is { Abandon: false, IsCompleted: false } &&
                    order.Identifier.Value is "steer" or "operatereactor" or "follow" or "return" or "prepareforexpedition")
                : !HasOrders(manager) &&
                    manager.CurrentObjective is not (AIObjectiveCombat { Priority: > 0f } or
                        AIObjectiveRescueAll { Priority: > 0f } or AIObjectiveExtinguishFires { Priority: > 0f }));

        internal static void Install(AIObjectiveManager manager, Character crew)
        {
            if (!AppliesTo(crew)) { return; }

            bool Replaced(AIObjective objective) =>
                objective is AIObjectiveFixLeaks and not AutoFixLeaks && objective.Identifier == "fixleaks" ||
                objective is AIObjectiveRepairItems and not AutoRepairSystems &&
                (objective.Identifier.Value is "repairsystems" or "repairmechanical" or "repairelectrical");

            manager.Objectives.RemoveAll(Replaced);
            foreach (var pending in manager.DelayedObjectives.Where(entry => Replaced(entry.Key)).ToArray())
            {
                CoroutineManager.StopCoroutines(pending.Value);
                manager.DelayedObjectives.Remove(pending.Key);
            }

            if (manager.GetObjective<AutoFixLeaks>() == null) { manager.AddObjective(new AutoFixLeaks(crew, manager)); }
            if (manager.GetObjective<AutoRepairSystems>() == null) { manager.AddObjective(new AutoRepairSystems(crew, manager)); }
            if (crew.Info?.Job?.Prefab.Identifier.Value is "engineer" or "mechanic" &&
                manager.GetObjective<InspectEquipment>() == null)
            {
                manager.AddObjective(new InspectEquipment(crew, manager));
            }
        }

        internal static float SelectPriority(bool hasTargets, bool hasOrder, bool fixesLeaks) =>
            DutyPriority(hasTargets, !hasOrder, fixesLeaks, emergency: false);

        internal static float DutyPriority(bool assigned, bool available, bool fixesLeaks, bool emergency) =>
            !assigned || !available ? 0f : emergency ? AIObjectiveManager.EmergencyObjectivePriority - 2f :
            AIObjectiveManager.LowestOrderPriority - (fixesLeaks ? 0.25f : 0.5f);

        internal static bool HasRepairDuty(Character crew) => AssignmentFor(crew) is { Urgency: > InspectionUrgency };

        private static Bid<Character, MapEntity> AssignmentFor(Character crew)
        {
            if (!Available(crew) || crew.CurrentHull?.Submarine is not { } submarine) { return null; }
            var plan = plans.GetOrCreateValue(submarine);
            double now = Timing.TotalTimeUnpaused;
            if (now >= plan.RefreshAfter)
            {
                plan.RefreshAfter = now + 1.0;
                Refresh(submarine, plan);
            }
            return plan.Assignments.GetValueOrDefault(crew);
        }

        private static void Refresh(Submarine submarine, RepairPlan plan)
        {
            var onboard = Character.CharacterList.Where(crew => crew.CurrentHull?.Submarine == submarine).ToArray();
            var occupiedHulls = onboard.Where(crew => !crew.IsDead && crew.IsOnPlayerTeam)
                .Select(crew => crew.CurrentHull).ToHashSet();
            var workers = onboard.Where(Available).Where(crew => !SecurityDefense.HasDefenseDuty(crew))
                .OrderBy(crew => crew.ID).ToArray();
            var gaps = Gap.GapList.Where(gap => gap.Submarine == submarine && !gap.Removed && gap.Open > 0f)
                .OrderBy(gap => gap.ID).ToArray();
            var equipment = Item.RepairableItems.Where(item => item.Submarine == submarine && !item.Removed &&
                item.CurrentHull?.Submarine == submarine).ToArray();
            var devices = equipment.Where(item => !item.IsFullCondition).OrderBy(item => item.ID).ToArray();
            double now = Timing.TotalTimeUnpaused;
            foreach (var room in plan.InspectedAt.Keys.Where(room => room.Removed).ToArray())
            {
                plan.InspectedAt.Remove(room);
            }
            var inspectionRooms = equipment.Where(item => item.ParentInventory == null)
                .GroupBy(item => item.CurrentHull)
                .Where(room => SafeToInspect(room.Key) &&
                    (!plan.InspectedAt.TryGetValue(room.Key, out double visit) || now >= visit + InspectionInterval))
                .OrderBy(room => room.Key.ID).ToArray();
            var claimed = new HashSet<MapEntity>();
            foreach (var crew in onboard.Where(crew => !crew.IsDead && HumanAIController.IsActive(crew) && !crew.IsIncapacitated && !crew.LockHands))
            {
                if (crew.AIController is not HumanAIController ai) { continue; }
                var manager = ai.ObjectiveManager;
                if (manager.CurrentObjective is not AutoFixLeaks &&
                    manager.GetFirstActiveObjective<AIObjectiveFixLeak>()?.Leak is { } gap) { claimed.Add(gap); }
                if (manager.CurrentObjective is not AutoRepairSystems &&
                    manager.GetFirstActiveObjective<AIObjectiveRepairItem>()?.Item is { } item) { claimed.Add(item); }
            }

            var candidates = new List<Bid<Character, MapEntity>>();
            foreach (var crew in workers)
            {
                if (crew.AIController is not HumanAIController ai) { continue; }
                var manager = ai.ObjectiveManager;
                var job = crew.Info?.Job?.Prefab.Identifier.Value;
                var previous = plan.Assignments.GetValueOrDefault(crew);
                bool ordered = HasOrders(manager);
                bool canRoutine = CanTakeDuty(manager, emergency: false);
                bool canRespond = CanTakeDuty(manager, emergency: true);
                if (manager.GetObjective<AutoFixLeaks>() is { IsAllowed: true } leaks)
                {
                    float skill = crew.GetSkillLevel(Mechanical);
                    foreach (var gap in gaps)
                    {
                        bool emergency = CriticalLeak(gap, occupiedHulls, previous?.Target == gap && previous.Urgency == 2);
                        if (!claimed.Contains(gap) && (emergency ? canRespond : canRoutine) && leaks.CanHandle(gap))
                        {
                            candidates.Add(new(crew, gap, emergency ? 2 : 1,
                                Score(crew, gap, skill, job is "mechanic" or "assistant", previous?.Target) +
                                AIObjectiveFixLeaks.GetLeakSeverity(gap) * 0.1f - (ordered ? 200f : 0f)));
                        }
                    }
                }
                if (canRoutine && manager.GetObjective<AutoRepairSystems>() is { IsAllowed: true } repairs)
                {
                    float mechanical = crew.GetSkillLevel(Mechanical);
                    float electrical = crew.GetSkillLevel(Electrical);
                    foreach (var item in devices)
                    {
                        if (claimed.Contains(item)) { continue; }
                        float repairPriority = AIObjectiveRepairItems.GetTargetPriority(item, crew, AIObjectiveRepairItems.RequiredSuccessFactor);
                        if (repairPriority <= 0f || !repairs.CanHandle(item)) { continue; }
                        bool mechanicalRepair = item.Repairables.Any(part => part.RequiredSkills.Any(skill => skill.Identifier == Mechanical));
                        bool electricalRepair = item.Repairables.Any(part => part.RequiredSkills.Any(skill => skill.Identifier == Electrical));
                        float skill = mechanicalRepair && electricalRepair ? Math.Min(mechanical, electrical) :
                            electricalRepair ? electrical : mechanical;
                        bool specialist = mechanicalRepair && job == "mechanic" || electricalRepair && job == "engineer";
                        candidates.Add(new(crew, item, 0, Score(crew, item, skill, specialist, previous?.Target) +
                            repairPriority * 0.25f));
                    }
                }
                if (canRoutine && manager.GetObjective<InspectEquipment>() is { IsAllowed: true } inspection)
                {
                    foreach (var room in inspectionRooms)
                    {
                        if (!inspection.CanVisit(room.Key) || !room.Any(inspection.CanInspectItem)) { continue; }

                        candidates.Add(new(crew, room.Key, InspectionUrgency,
                            Score(crew, room.Key, 0f, specialist: false, previous?.Target)));
                    }
                }
            }
            plan.Assignments = Assign(candidates);
        }

        private static bool CriticalLeak(Gap gap, HashSet<Hull> occupiedHulls, bool responding) =>
            gap.FlowTargetHull is { } hull &&
            IsCriticalFlooding(!gap.IsRoomToRoom, occupiedHulls.Contains(hull), gap.Open, hull.WaterPercentage, responding);

        internal static bool IsCriticalFlooding(bool exterior, bool occupied, float opening, float flooding, bool responding) =>
            exterior && occupied && opening >= (responding ? 0.25f : 0.5f) && flooding >= (responding ? 35f : 50f);

        private static float Score(Character worker, MapEntity target, float skill, bool specialist, MapEntity previous) =>
            Math.Min(skill, 100f) * 0.5f + (specialist ? 20f : 0f) + (target == previous ? 80f : 0f) -
            Math.Min(Vector2.Distance(worker.WorldPosition, target.WorldPosition) / 100f, 40f);

        private static bool CanRetry<T>(Dictionary<T, double> failures, T target)
        {
            if (!failures.TryGetValue(target, out double retry)) { return true; }
            if (Timing.TotalTimeUnpaused < retry) { return false; }
            failures.Remove(target);
            return true;
        }

        internal static float GetInspectionPriority(bool assigned, bool available) =>
            assigned && available ? InspectionPriority : 0f;

        private static bool SafeToInspect(Hull hull) =>
            !hull.Removed && hull.WaterPercentage < 25f && hull.FireSources.Count == 0;

        private sealed class InspectEquipment : AIObjective
        {
            private readonly Dictionary<Hull, double> failures = new();
            private Hull destination;
            private AIObjectiveGoTo route;
            private double deadline;

            public override Identifier Identifier { get; set; } = "inspectequipment".ToIdentifier();
            public override bool AbandonWhenCannotCompleteSubObjectives => false;

            internal InspectEquipment(Character crew, AIObjectiveManager manager)
                : base(crew, manager, 1f, Identifier.Empty) { }

            internal bool CanVisit(Hull hull) => SafeToInspect(hull) && CanRetry(failures, hull);

            internal bool CanInspectItem(Item item) =>
                !item.Removed && item.ParentInventory == null && item.IsInteractable(character) &&
                item.HasAccess(character) && !item.IgnoreByAI(character);

            protected override bool CheckObjectiveState() => false;

            protected override float GetPriority()
            {
                var target = IsAllowed && CanTakeDuty(objectiveManager, emergency: false) &&
                    AssignmentFor(character)?.Target is Hull hull && CanVisit(hull) ? hull : null;
                if (target != destination)
                {
                    RemoveSubObjective(ref route);
                    destination = target;
                }
                return GetInspectionPriority(target != null, available: true);
            }

            public override void OnDeselected()
            {
                RemoveSubObjective(ref route);
                base.OnDeselected();
            }

            protected override void Act(float deltaTime)
            {
                if (destination == null || route != null) { return; }

                var equipment = Item.RepairableItems
                    .Where(item => item.CurrentHull == destination && CanInspectItem(item))
                    .OrderBy(item => Vector2.DistanceSquared(character.WorldPosition, item.WorldPosition))
                    .ThenBy(item => item.ID)
                    .FirstOrDefault();
                if (equipment == null)
                {
                    FinishVisit(arrived: false);
                    return;
                }
                route = new AIObjectiveGoTo(equipment, character, objectiveManager,
                    repeat: false, getDivingGearIfNeeded: false);
                route.SpeakIfFails = false;
                deadline = Timing.TotalTimeUnpaused + 25d;
                AddSubObjective(route);
            }

            public override void Update(float deltaTime)
            {
                base.Update(deltaTime);
                if (route == null || destination == null) { return; }
                if (route.IsCompleted || route.Abandon || Timing.TotalTimeUnpaused >= deadline)
                {
                    FinishVisit(route.IsCompleted && character.CurrentHull == destination);
                }
            }

            private void FinishVisit(bool arrived)
            {
                var plan = plans.GetOrCreateValue(destination.Submarine);
                double now = Timing.TotalTimeUnpaused;
                if (arrived)
                {
                    plan.InspectedAt[destination] = now;
                    if (route?.Target is Item inspected) { ReportDamagedDevice(character, inspected); }
                }
                else { failures[destination] = now + 30d; }
                plan.RefreshAfter = 0d;
                destination = null;
                RemoveSubObjective(ref route);
            }
        }

        private sealed class AutoFixLeaks : AIObjectiveFixLeaks
        {
            private readonly Dictionary<Gap, double> failures = new();
            private Gap selected;

            internal AutoFixLeaks(Character crew, AIObjectiveManager manager) : base(crew, manager)
            {
                Identifier = "fixleaks".ToIdentifier();
            }

            internal bool CanHandle(Gap gap) => !gap.Removed && gap.Open > 0f &&
                CanRetry(failures, gap) && !ignoreList.Contains(gap) && IsValidTarget(gap);

            protected override AIObjective ObjectiveConstructor(Gap gap)
            {
                var objective = base.ObjectiveConstructor(gap);
                objective.Abandoned += () => failures[gap] = Timing.TotalTimeUnpaused + 15d;
                return objective;
            }

            protected override void FindTargets()
            {
                if (selected != null && CanHandle(selected)) { Targets.Add(selected); }
            }

            public override void Reset()
            {
                Objectives.Clear();
                base.Reset();
            }

            protected override float GetPriority()
            {
                var bid = IsAllowed ? AssignmentFor(character) : null;
                bool emergency = bid?.Urgency == 2;
                bool available = CanTakeDuty(objectiveManager, emergency);
                var target = available && bid?.Target is Gap gap && CanHandle(gap) ? gap : null;
                if (selected != target)
                {
                    selected = target;
                    // Retarget the native loop without clearing its failed-target ignore list.
                    subObjectives.Clear();
                    Objectives.Clear();
                    UpdateTargets();
                }
                return DutyPriority(target != null, available, fixesLeaks: true, emergency);
            }
        }

        private static bool ReportDamagedDevice(Character reporter, Item item)
        {
            if (item.CurrentHull is not { } hull || hull != reporter.CurrentHull ||
                GameMain.GameSession?.CrewManager is not { } crew ||
                !AIObjectiveRepairItems.IsValidTarget(item, reporter) ||
                item.Repairables.FirstOrDefault(part => part.IsBelowRepairIconThreshold) is not { } damaged)
            {
                return false;
            }

            var order = new Order(OrderPrefab.Prefabs["reportbrokendevices"], hull, damaged, reporter, false);
            if (crew.AddOrder(order, order.FadeOutTime))
            {
                reporter.Speak(order.GetChatMessage(string.Empty, hull.DisplayName.Value, false, Identifier.Empty, true),
                    ChatMessageType.Order, 0f, Identifier.Empty, 0f);
            }
            return true;
        }

        private sealed class CoordinatedRepairItem : AIObjectiveRepairItem
        {
            private bool reported;

            internal CoordinatedRepairItem(Character crew, Item item, AIObjectiveManager manager, float priorityModifier)
                : base(crew, item, manager, priorityModifier, false) { }

            protected override void Act(float deltaTime)
            {
                if (!reported) { reported = ReportDamagedDevice(character, Item); }
                base.Act(deltaTime);
            }
        }

        private sealed class AutoRepairSystems : AIObjectiveRepairItems
        {
            private readonly float targetPriorityModifier = 0.5f / JobPrefab.ItemRepairPriorities.Values.Append(1f).Max();
            private readonly Dictionary<Item, double> failures = new();
            private Item selected;

            internal AutoRepairSystems(Character crew, AIObjectiveManager manager) : base(crew, manager)
            {
                Identifier = (crew.Info?.Job?.Prefab.Identifier.Value switch
                {
                    "engineer" => "repairelectrical",
                    "mechanic" => "repairmechanical",
                    _ => "repairsystems"
                }).ToIdentifier();
            }

            internal bool CanHandle(Item item) => !item.Removed && !item.IsFullCondition &&
                CanRetry(failures, item) && !ignoreList.Contains(item) && IsValidTarget(item);

            protected override AIObjective ObjectiveConstructor(Item item)
            {
                var objective = new CoordinatedRepairItem(character, item, objectiveManager, targetPriorityModifier);
                objective.Abandoned += () => failures[item] = Timing.TotalTimeUnpaused + 15d;
                return objective;
            }

            protected override void FindTargets()
            {
                if (selected != null && CanHandle(selected)) { Targets.Add(selected); }
            }

            public override void Reset()
            {
                Objectives.Clear();
                base.Reset();
            }

            protected override float GetPriority()
            {
                bool available = IsAllowed && CanTakeDuty(objectiveManager, emergency: false);
                var target = available && AssignmentFor(character)?.Target is Item item && CanHandle(item) ? item : null;
                if (selected != target)
                {
                    selected = target;
                    subObjectives.Clear();
                    Objectives.Clear();
                    UpdateTargets();
                }
                return DutyPriority(target != null, available, fixesLeaks: false, emergency: false);
            }
        }
    }
}
