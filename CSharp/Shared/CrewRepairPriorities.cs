using System.Linq;
using Barotrauma;

namespace EnhancedExperience
{
    internal static class CrewRepairPriorities
    {
        internal static bool AppliesTo(Character crew) =>
            GameMain.NetworkMember?.IsClient != true && crew.IsBot && crew.IsOnPlayerTeam && !crew.IsDead;

        internal static void Install(AIObjectiveManager manager, Character crew)
        {
            if (!AppliesTo(crew)) { return; }

            var job = crew.Info?.Job?.Prefab.Identifier.Value;
            if (job != "assistant" && job != "mechanic" && job != "engineer") { return; }

            // Only replace autonomous job objectives. Player orders live in CurrentOrders.
            bool Replaced(AIObjective objective) =>
                job != "engineer" && objective is AIObjectiveFixLeaks and not AutoFixLeaks &&
                objective.Identifier == "fixleaks" ||
                job != "assistant" && objective is AIObjectiveRepairItems and not AutoRepairSystems &&
                (objective.Identifier == "repairsystems" || objective.Identifier == "repairmechanical" ||
                 objective.Identifier == "repairelectrical");

            manager.Objectives.RemoveAll(Replaced);
            foreach (var pending in manager.DelayedObjectives.Where(entry => Replaced(entry.Key)).ToArray())
            {
                CoroutineManager.StopCoroutines(pending.Value);
                manager.DelayedObjectives.Remove(pending.Key);
            }

            if (job != "engineer" && manager.GetObjective<AutoFixLeaks>() == null)
            {
                manager.AddObjective(new AutoFixLeaks(crew, manager));
            }
            if (job != "assistant" && manager.GetObjective<AutoRepairSystems>() == null)
            {
                manager.AddObjective(new AutoRepairSystems(crew, manager, job == "engineer"));
            }
        }

        internal static float SelectPriority(bool hasTargets, bool hasOrder, bool fixesLeaks)
        {
            if (!hasTargets || hasOrder) { return 0f; }

            // Ordinary autonomous loops cap at 59, normal orders start at 60, and emergencies can go higher.
            return AIObjectiveManager.LowestOrderPriority - (fixesLeaks ? 0.25f : 0.5f);
        }

        private static bool HasActiveOrder(AIObjectiveManager manager) =>
            manager.ForcedOrder != null || manager.CurrentOrder?.Priority > 0f;

        private sealed class AutoFixLeaks : AIObjectiveFixLeaks
        {
            internal AutoFixLeaks(Character crew, AIObjectiveManager manager) : base(crew, manager)
            {
                Identifier = "fixleaks".ToIdentifier();
            }

            protected override void FindTargets()
            {
                var assigned = Character.CharacterList
                    .Where(crew => crew != character && crew.TeamID == character.TeamID &&
                        HumanAIController.IsActive(crew) && crew.AIController is HumanAIController)
                    .Select(crew => ((HumanAIController)crew.AIController).ObjectiveManager
                        .GetFirstActiveObjective<AIObjectiveFixLeak>()?.Leak)
                    .ToHashSet();

                foreach (var gap in GetList())
                {
                    if (!ignoreList.Contains(gap) && !assigned.Contains(gap) && IsValidTarget(gap))
                    {
                        Targets.Add(gap);
                    }
                }
            }

            protected override float GetPriority() => IsAllowed
                ? SelectPriority(Targets.Any(gap => gap.Open > 0f && !ignoreList.Contains(gap)),
                    HasActiveOrder(objectiveManager), fixesLeaks: true)
                : base.GetPriority();
        }

        private sealed class AutoRepairSystems : AIObjectiveRepairItems
        {
            private readonly float targetPriorityModifier;

            internal AutoRepairSystems(Character crew, AIObjectiveManager manager, bool electrical) : base(crew, manager)
            {
                RelevantSkill = (electrical ? "electrical" : "mechanical").ToIdentifier();
                Identifier = (electrical ? "repairelectrical" : "repairmechanical").ToIdentifier();

                // Leave room for devotion in the native score. Otherwise heavily damaged devices with
                // different repair weights can both reach the native cap before either repair starts.
                float highestWeight = JobPrefab.ItemRepairPriorities.Values.Append(1f).Max();
                targetPriorityModifier = 0.5f / highestWeight;
            }

            protected override AIObjective ObjectiveConstructor(Item item) =>
                new AIObjectiveRepairItem(character, item, objectiveManager, targetPriorityModifier, false);

            protected override void FindTargets()
            {
                // Use native viability, skill and ownership checks without requiring a manual report.
                foreach (var item in GetList())
                {
                    if (!ignoreList.Contains(item) && IsValidTarget(item))
                    {
                        Targets.Add(item);
                    }
                }
            }

            protected override float GetPriority() => IsAllowed
                ? SelectPriority(Targets.Any(item => !item.IsFullCondition && !ignoreList.Contains(item)),
                    HasActiveOrder(objectiveManager), fixesLeaks: false)
                : base.GetPriority();
        }
    }
}
