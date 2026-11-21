using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;

[assembly: IgnoresAccessChecksTo("Barotrauma")]
[assembly: IgnoresAccessChecksTo("BarotraumaCore")]
[assembly: IgnoresAccessChecksTo("DedicatedServer")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public string AssemblyName { get; }
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;
    }
}

namespace EnhancedExperience
{
    public sealed class ExpeditionPlugin : IAssemblyPlugin
    {
        private readonly Harmony harmony = new Harmony("EnhancedExperience.PrepareForExpedition");

        public void Initialize()
        {
            ExteriorFollow.Install(harmony);
            SecurityDefense.InstallPatches(harmony);
            harmony.Patch(
                AccessTools.Method(typeof(AIObjectiveManager), nameof(AIObjectiveManager.CreateAutonomousObjectives)),
                prefix: new HarmonyMethod(typeof(ExpeditionPlugin), nameof(PreserveMaintenancePatrol)),
                postfix: new HarmonyMethod(typeof(ExpeditionPlugin), nameof(InstallCrewObjectives)));
            harmony.Patch(
                AccessTools.Method(typeof(AIObjectiveManager), nameof(AIObjectiveManager.CreateObjective),
                    new[] { typeof(Order), typeof(float) }),
                postfix: new HarmonyMethod(typeof(ExpeditionPlugin), nameof(ReplaceOrderObjective)));
        }

        private static void PreserveMaintenancePatrol(AIObjectiveManager __instance, out MaintenancePatrol __state) =>
            __state = __instance.GetObjective<MaintenancePatrol>();

        private static void InstallCrewObjectives(AIObjectiveManager __instance, Character ___character,
            MaintenancePatrol __state)
        {
            CrewRepairPriorities.Install(__instance, ___character);
            MaintenancePatrol.InstallForCrew(__instance, ___character, __state);
            SecurityDefense.InstallForCrew(__instance, ___character);
        }

        private static void ReplaceOrderObjective(Order order, float priorityModifier, AIObjectiveManager __instance,
            Character ___character, ref AIObjective __result)
        {
            if (__result == null || GameMain.NetworkMember?.IsClient == true) { return; }
            if (__result is AIObjectiveCleanupItems cleanup && order.Identifier.Value == "cleanupitems" &&
                ___character.IsBot && MedicalCleanupObjective.IsDoctor(___character))
            {
                __result = new MedicalCleanupObjective(___character, __instance, cleanup.prioritizedItems, priorityModifier)
                {
                    Identifier = order.Identifier,
                    IgnoreAtOutpost = cleanup.IgnoreAtOutpost
                };
            }
            else if (__result is AIObjectiveDeconstructItems && order.Identifier.Value == "deconstructitems" &&
                GameMain.NetworkMember == null && ___character.IsBot && MineralDeconstructionObjective.IsMechanic(___character))
            {
                __result = new MineralDeconstructionObjective(___character, __instance, priorityModifier)
                {
                    Identifier = order.Identifier,
                    IgnoreAtOutpost = __result.IgnoreAtOutpost
                };
            }
            else if (__result is AIObjectiveDeconstructItems && order.Identifier.Value == "deconstructitems" &&
                ___character.IsBot && MedicalCleanupObjective.IsDoctor(___character))
            {
                __result = new MedicalDeconstructionObjective(___character, __instance, priorityModifier)
                {
                    Identifier = order.Identifier,
                    IgnoreAtOutpost = __result.IgnoreAtOutpost
                };
            }
            else if (__result is AIObjectivePrepare && order.Identifier.Value == "prepareforexpedition")
            {
                __result = new AIObjectivePrepareForExpedition(___character, __instance, priorityModifier)
                {
                    Identifier = order.Identifier,
                    IgnoreAtOutpost = __result.IgnoreAtOutpost
                };
            }
        }

        // Call on the authoritative simulation thread after issuing the order. Null clears the plan.
        public static bool TrySetPlannedDepth(object character, float? metres)
        {
            if (GameMain.NetworkMember?.IsClient == true ||
                (metres is float value && (!float.IsFinite(value) || value < 0)) ||
                character is not Character { AIController: HumanAIController ai } ||
                ai.ObjectiveManager.GetOrder<AIObjectivePrepareForExpedition>() is not { } objective)
            {
                return false;
            }

            objective.PlannedDepth = metres;
            return true;
        }

        public void OnLoadCompleted()
        {
            // LuaCs may activate the plugin after the round has created its crew.
            foreach (var crew in Character.CharacterList)
            {
                if (crew.AIController is HumanAIController ai)
                {
                    InstallCrewObjectives(ai.ObjectiveManager, crew, null);
                }
            }
        }
        public void PreInitPatching() { }
        public void Dispose()
        {
            ExteriorFollow.Clear();
            SecurityDefense.Clear();
            harmony.UnpatchSelf();
        }
    }
}
