using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace EnhancedExperience
{
    internal sealed class MedicalCleanupObjective : AIObjectiveCleanupItems
    {
        private static readonly Identifier Poison = "poison".ToIdentifier();
        private static readonly Identifier Medical = "medical".ToIdentifier();
        private static readonly Identifier Doctor = "medicaldoctor".ToIdentifier();
        private static readonly Identifier MedicineCabinet = "medcabinet".ToIdentifier();
        private static readonly Identifier ToxinCabinet = "toxcabinet".ToIdentifier();
        private static readonly Identifier MedicineTag = "medcab".ToIdentifier();
        private static readonly Identifier ToxinTag = "toxcab".ToIdentifier();
        private static readonly Identifier MobileStorage = "mobilecontainer".ToIdentifier();
        private static readonly Identifier Crate = "crate".ToIdentifier();

        private readonly HashSet<Item> visited = new HashSet<Item>();
        private readonly Dictionary<Item, double> medicalRetries = new Dictionary<Item, double>();
        private Item[] cabinets = Array.Empty<Item>();
        private Item[] extraTargets = Array.Empty<Item>();
        private Submarine scannedSubmarine;
        private double nextScan;
        private double nextVisit;
        private double nextCompletionCheck;

        internal MedicalCleanupObjective(Character character, AIObjectiveManager manager,
            IEnumerable<Item> prioritized, float priorityModifier)
            : base(character, manager, prioritized, priorityModifier) { }

        internal static bool IsDoctor(Character character) => character.Info?.Job?.Prefab.Identifier == Doctor;
        private bool IsActive => IsAllowed && objectiveManager.IsOrder(this) && objectiveManager.CurrentObjective == this;

        private enum Storage { Vanilla, Medicine, Toxin }

        private static Storage Classify(Item item) => item.HasTag(Poison) ? Storage.Toxin :
            item.HasTag(Medical) || item.Prefab.Category.HasFlag(MapEntityCategory.Medical) ? Storage.Medicine : Storage.Vanilla;

        private static Storage CabinetType(Item item)
        {
            if (item.Prefab.Identifier == ToxinCabinet || item.HasTag(ToxinTag)) { return Storage.Toxin; }
            if (item.Prefab.Identifier == MedicineCabinet || item.HasTag(MedicineTag)) { return Storage.Medicine; }
            return Storage.Vanilla;
        }

        private void RefreshStorage()
        {
            var submarine = character.Submarine;
            var now = Timing.TotalTimeUnpaused;
            if (scannedSubmarine == submarine && now < nextScan) { return; }

            scannedSubmarine = submarine;
            nextScan = now + 2;
            var items = submarine?.GetItems(alsoFromConnectedSubs: true);
            cabinets = items?.Where(item => !item.Removed && item.ParentInventory == null &&
                CabinetType(item) != Storage.Vanilla && item.GetComponent<ItemContainer>() != null).ToArray() ?? Array.Empty<Item>();
            extraTargets = items?.Where(item => !item.Removed && item.Submarine == submarine &&
                (item.Container != null || !item.Prefab.PreferredContainers.Any()) && Classify(item) != Storage.Vanilla && item.GetComponent<Pickable>() is { IsAttached: false, CanBePicked: true })
                .ToArray() ?? Array.Empty<Item>();
        }

        protected override IEnumerable<Item> GetList()
        {
            RefreshStorage();
            return base.GetList().Concat(extraTargets).Distinct();
        }

        protected override bool IsValidTarget(Item target) => IsValidTarget(target, requireDestination: true);

        private IEnumerable<Item> RemainingWork() =>
            GetList().Where(item => IsValidTarget(item, requireDestination: false));

        internal Item[] GetRemainingWorkForTest() => RemainingWork().ToArray();

        internal string DescribeRemainingWork()
        {
            var pending = RemainingWork().Take(8)
                .Select(item => $"{item.Prefab.Identifier}#{item.ID} in {item.Container?.Prefab.Identifier.ToString() ?? "no container"}");
            return $"allowed={IsAllowed}, safe={IsSafe(character.CurrentHull)}, children={SubObjectives.Count()}, " +
                $"pending=[{string.Join(", ", pending)}]";
        }

        private bool IsValidTarget(Item target, bool requireDestination)
        {
            if (target.Removed || target.ParentInventory?.Locked == true || target.GetRootInventoryOwner() is Character owner && owner != character)
            {
                return false;
            }

            var storage = Classify(target);
            if (storage != Storage.Vanilla && target.Container != null && !target.IsOwnedBy(character))
            {
                if (CabinetType(target.Container) == storage)
                {
                    // Keep an existing child long enough to finish its deposit callback.
                    return requireDestination && Objectives.ContainsKey(target);
                }
                return CanCollectFromStorage(target, storage) &&
                    (!requireDestination || Objectives.ContainsKey(target) || CanPutInInventory(character, target, allowWearing: false) &&
                        FindCabinet(target, storage) != null);
            }

            if (target.Prefab.PreferredContainers.Any()) { return base.IsValidTarget(target); }
            return storage != Storage.Vanilla && (Objectives.ContainsKey(target) && target.IsOwnedBy(character) ||
                IsValidTarget(target, character, checkInventory: true) && IsSafe(target.CurrentHull));
        }

        private bool CanCollectFromStorage(Item item, Storage storage)
        {
            if (item.GetRootInventoryOwner() is not Item root || root == item || root.Submarine != character.Submarine ||
                (CabinetType(item.Container) == storage || CabinetType(root) == storage) || !IsSafe(root.CurrentHull) ||
                !IsValidTarget(item, character, checkInventory: false, requireValidContainer: false))
            {
                return false;
            }

            for (var child = item; child.ParentInventory is ItemInventory inventory; child = inventory.Container.Item)
            {
                var container = inventory.Container;
                var owner = container.Item;
                if (inventory.Locked || owner.NonInteractable || owner.IsClaimedByBallastFlora ||
                    owner.HasTag(Tags.DontTakeItems) || !owner.HasAccess(character) || !container.IsAccessible() ||
                    !container.HasRequiredItems(character, addMessage: false) ||
                    ((owner.GetComponent<Wearable>() != null || owner.GetComponent<Holdable>() != null) &&
                        !owner.HasTag(MobileStorage) && !owner.HasTag(Crate)))
                {
                    return false;
                }
            }
            return true;
        }

        protected override AIObjective ObjectiveConstructor(Item item)
        {
            var storage = Classify(item);
            var objective = storage == Storage.Vanilla
                ? base.ObjectiveConstructor(item)
                : new MedicalCleanupItem(item, character, objectiveManager, this, storage)
                {
                    IsPriority = prioritizedItems.Contains(item)
                };
            if (storage != Storage.Vanilla)
            {
                objective.Abandoned += () => medicalRetries[item] = Timing.TotalTimeUnpaused + 25;
            }
            objective.AbortCondition = _ => item.ParentInventory?.Locked == true ||
                item.GetRootInventoryOwner() is Character owner && owner != character;
            return objective;
        }

        private bool IsSafe(Hull hull) => hull != null && hull.FireSources.Count == 0 &&
            !HumanAIController.UnsafeHulls.Contains(hull);

        private bool CanAccess(Item cabinet) =>
            !cabinet.Removed && !cabinet.NonInteractable && cabinet.ParentInventory == null && !cabinet.IsClaimedByBallastFlora &&
            IsItemInsideValidSubmarine(cabinet, character) && IsSafe(cabinet.CurrentHull) &&
            cabinet.HasAccess(character) && cabinet.GetComponent<ItemContainer>() is { } container &&
            container.IsAccessible() && !container.Inventory.Locked && container.HasRequiredItems(character, addMessage: false);

        private ItemContainer FindCabinet(Item item, Storage storage, HashSet<Item> rejected = null)
        {
            RefreshStorage();
            return cabinets.Where(cabinet => CabinetType(cabinet) == storage && rejected?.Contains(cabinet) != true &&
                    cabinet != item.Container && CanAccess(cabinet))
                .OrderBy(cabinet => Vector2.DistanceSquared(item.WorldPosition, cabinet.WorldPosition))
                .ThenBy(cabinet => cabinet.ID)
                .Select(cabinet => cabinet.GetComponent<ItemContainer>())
                .FirstOrDefault(container => container.Inventory.CanBePut(item));
        }

        private void OrganizeAfterDeposit(Item cabinet)
        {
            if (!IsActive || !IsSafe(character.CurrentHull) || cabinet == null ||
                CabinetType(cabinet) == Storage.Vanilla || !CanAccess(cabinet) ||
                !character.CanInteractWith(cabinet, checkLinked: false))
            {
                return;
            }

            MedicalCabinetOrganizer.Organize(cabinet.GetComponent<ItemContainer>());
            visited.Add(cabinet);
        }

        protected override void OnObjectiveCompleted(AIObjective objective, Item target)
        {
            if (target.Container is Item cabinet)
            {
                visited.Remove(cabinet);
                OrganizeAfterDeposit(cabinet);
            }
            base.OnObjectiveCompleted(objective, target);
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            double now = Timing.TotalTimeUnpaused;
            if (now < nextVisit) { return; }
            nextVisit = now + 0.5;
            foreach (var retry in medicalRetries.Where(entry => entry.Key.Removed || entry.Value <= now).ToArray())
            {
                ignoreList.Remove(retry.Key);
                medicalRetries.Remove(retry.Key);
            }

            if (!IsSafe(character.CurrentHull)) { return; }
            if (IsAllowed && now >= nextCompletionCheck && objectiveManager.IsOrder(this))
            {
                nextCompletionCheck = now + 2;
                if (!SubObjectives.Any() && !RemainingWork().Any())
                {
                    DismissEmptyOrder(this);
                }
            }

            if (!IsActive) { return; }
            RefreshStorage();
            visited.RemoveWhere(cabinet => cabinet.Removed ||
                !character.CanInteractWith(cabinet, checkLinked: false));
            foreach (var cabinet in cabinets)
            {
                if (!visited.Contains(cabinet) && cabinet.CurrentHull == character.CurrentHull)
                {
                    OrganizeAfterDeposit(cabinet);
                }
            }
        }

        internal static void DismissEmptyOrder(AIObjective objective)
        {
            // The objective manager enumerates CurrentOrders during Update.
            CoroutineManager.Invoke(() =>
            {
                if (objective.objectiveManager.CurrentOrders.Any(order => order.Objective == objective))
                {
                    objective.Abandon = true; // Uses the game's order dismissal and synchronization.
                }
            }, 0.01f);
        }

        private sealed class MedicalCleanupItem : AIObjectiveCleanupItem
        {
            private readonly MedicalCleanupObjective parent;
            private readonly Storage storage;
            private readonly HashSet<Item> rejected = new HashSet<Item>();
            private AIObjectiveMoveItem move;
            private bool useVanilla;
            private readonly bool requiresCabinet;

            internal MedicalCleanupItem(Item item, Character character, AIObjectiveManager manager,
                MedicalCleanupObjective parent, Storage storage)
                : base(item, character, manager, parent.PriorityModifier)
            {
                this.parent = parent;
                this.storage = storage;
                requiresCabinet = item.Container != null;
            }

            protected override bool CheckObjectiveState()
            {
                if (item.Container is Item cabinet && CabinetType(cabinet) == storage &&
                    !item.IsOwnedBy(character))
                {
                    return true;
                }
                if (!requiresCabinet) { return base.CheckObjectiveState(); }
                if (IsCompleted) { return true; }
                if (!item.IsOwnedBy(character) && !parent.CanCollectFromStorage(item, storage))
                {
                    Abandon = true;
                }
                return false;
            }

            protected override void Act(float deltaTime)
            {
                if (!parent.IsActive || SubObjectives.Any()) { return; }
                if (useVanilla)
                {
                    base.Act(deltaTime);
                    return;
                }

                var cabinet = parent.FindCabinet(item, storage, rejected);
                if (cabinet == null)
                {
                    if (requiresCabinet || storage == Storage.Toxin || !item.Prefab.PreferredContainers.Any())
                    {
                        Abandon = true;
                        return;
                    }
                    useVanilla = true;
                    base.Act(deltaTime);
                    return;
                }

                move = null;
                TryAddSubObjective(ref move, () => new AIObjectiveMoveItem(character, item, objectiveManager,
                    targetContainer: cabinet)
                {
                    Equip = false,
                    TakeWholeStack = false,
                    DropIfFails = false,
                    AbortCondition = _ => !parent.IsActive || !parent.CanAccess(cabinet.Item) ||
                        item.GetRootInventoryOwner() is Character owner && owner != character ||
                        (!cabinet.Inventory.Contains(item) && !cabinet.Inventory.CanBePut(item))
                },
                onCompleted: () => IsCompleted = true,
                onAbandon: () => rejected.Add(cabinet.Item));
            }
        }
    }
}
