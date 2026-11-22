using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace EnhancedExperience
{
    internal sealed class CrewDeconstructionObjective : AIObjectiveDeconstructItems
    {
        private static readonly Identifier Ore = "ore".ToIdentifier();
        private static readonly Identifier Plant = "plant".ToIdentifier();
        private static readonly Identifier PlantItem = "plantitem".ToIdentifier();
        private static readonly Identifier Seed = "seed".ToIdentifier();

        internal static bool IsResource(Identifier id, IEnumerable<Identifier> tags) =>
            id.Value is "oxygeniteshard" or "sulphuriteshard" ||
            tags.Any(tag => tag == Ore || tag == Plant || tag == PlantItem || tag == Seed);

        private static bool IsAutomaticTarget(ItemPrefab prefab) => IsResource(prefab.Identifier, prefab.Tags) &&
            prefab.DeconstructItems.Any(recipe => recipe.Amount > 0 && recipe.RequiredOtherItem.Length == 0 &&
                recipe.ItemIdentifier != prefab.Identifier);

        private static readonly ConditionalWeakTable<Item, WeakReference<CrewDeconstructionObjective>>
            Claims = new ConditionalWeakTable<Item, WeakReference<CrewDeconstructionObjective>>();

        private readonly HashSet<Item> rejected = new HashSet<Item>();
        private (Item Item, Deconstructor Machine)? proposal;
        private Item target;
        private Deconstructor machine;
        private AIObjectiveGoTo route;
        private Item routeDestination;
        private bool collectedTarget;
        private Item processingItem;
        private Deconstructor processingMachine;
        private double nextSearch;
        private double deadline;

        public CrewDeconstructionObjective(Character character, AIObjectiveManager manager, float priorityModifier)
            : base(character, manager, priorityModifier) { }

        private bool IsOrdered => objectiveManager.IsOrder(this);
        private bool CanWork => IsOrdered && IsAllowed && !character.IsIncapacitated &&
            !character.IsLowInOxygen && Safe(character.CurrentHull) &&
            (character.IsProtectedFromPressure || character.CurrentHull.LethalPressure <= 0);
        private bool Safe(Hull hull) => hull != null && hull.FireSources.Count == 0 &&
            !HumanAIController.UnsafeHulls.Contains(hull);

        protected override float GetTargetPriority()
        {
            float manual = base.GetTargetPriority();
            return manual > 0 || !CanWork || target == null && proposal == null
                ? manual : objectiveManager.GetOrderPriority(this);
        }

        protected override bool ResetWhenClearingIgnoreList => false;

        public override void Reset()
        {
            // The vanilla loop resets while selected as well as when retrying manual work.
            // Keep the unclaimed proposal for the upcoming Act call.
            Release();
            base.Reset();
        }

        public override void OnDeselected()
        {
            Release();
            proposal = null;
            base.OnDeselected();
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            double now = Timing.TotalTimeUnpaused;
            if (processingItem != null)
            {
                if (!processingItem.Removed && processingMachine.IsActive && processingItem.ParentInventory == processingMachine.InputContainer.Inventory) { return; }
                processingItem = null;
                processingMachine = null;
            }
            if (!CanWork || Targets.Count > 0)
            {
                Release();
                proposal = null;
                return;
            }

            if (target != null)
            {
                if (Item.DeconstructItems.Contains(target) || !CanFetch(target) ||
                    IsClaimed(target))
                {
                    Release();
                }
                else if (IsClaimed(machine.Item) || !IsReady(machine))
                {
                    Release();
                }
                else if (route != null && (routeDestination != Destination || CanReach(Destination)))
                {
                    ClearRoute();
                }
                else if (now >= deadline || route?.Abandon == true)
                {
                    Fail(Destination);
                }
                return;
            }

            if (now < nextSearch) { return; }
            nextSearch = now + 2;
            rejected.RemoveWhere(item => item.Removed);
            var machines = GetMachines().ToArray();
            var candidates = machines.Length == 0 ? Array.Empty<Item>() :
                Item.ItemList.Where(item => !Item.DeconstructItems.Contains(item) && CanFetch(item) &&
                !rejected.Contains(item) && !rejected.Contains(Root(item)) &&
                machines.Any(device => SupportsRecipe(device, item))).ToArray();
            proposal = FindWork(candidates, machines);
            if (proposal == null && candidates.Length == 0 && !SubObjectives.Any() &&
                !GetList().Any(item => !ignoreList.Contains(item) && IsValidTarget(item)))
            {
                MedicalCleanupObjective.DismissEmptyOrder(this);
            }
        }

        protected override void Act(float deltaTime)
        {
            if (!CanWork || Targets.Count > 0) { return; }
            if (target == null)
            {
                if (proposal is not { } next) { return; }
                proposal = null;
                if (IsClaimed(next.Item) || IsClaimed(next.Machine.Item) ||
                    Item.DeconstructItems.Contains(next.Item) || !CanFetch(next.Item) ||
                    !CanUse(next.Machine, next.Item)) { return; }

                target = next.Item;
                machine = next.Machine;
                Claim(target);
                Claim(machine.Item);
                deadline = Timing.TotalTimeUnpaused + 25;
            }

            if (!CanFetch(target) || Item.DeconstructItems.Contains(target) || IsClaimed(target) || IsClaimed(machine.Item))
            {
                Release();
                return;
            }

            // A completed route can end short of actual interaction.
            if (route != null)
            {
                bool canReach = routeDestination != Destination || CanReach(Destination);
                ClearRoute();
                if (!canReach)
                {
                    Fail(Destination);
                    return;
                }
            }

            if (!target.IsOwnedBy(character))
            {
                Item source = Root(target);
                if (!CanReach(source))
                {
                    GoTo(source);
                    return;
                }

                int slot = FindCarrySlot(target);
                if (slot < 0 || !character.Inventory.TryPutItem(target, slot, false, false, character))
                {
                    Fail(target);
                    return;
                }
                collectedTarget = true;
                deadline = Timing.TotalTimeUnpaused + 25;
            }

            if (!CanReach(machine.Item))
            {
                GoTo(machine.Item);
                return;
            }

            // Starting a deconstructor processes its entire input queue. Use an empty input
            // and insert one validated item in the same simulation tick as activation.
            var input = machine.InputContainer.Inventory;
            if (!CanUse(machine, target) || !CanFetch(target) || Item.DeconstructItems.Contains(target) ||
                !input.TryPutItem(target, character))
            {
                Fail(machine.Item);
                return;
            }
            if (target.ParentInventory == input && input.AllItems.All(item => item == target))
            {
                machine.SetActive(active: true, user: character, createNetworkEvent: true);
                if (machine.IsActive)
                {
                    processingItem = target;
                    processingMachine = machine;
                }
            }
            else
            {
                if (target.ParentInventory == input) { target.Drop(character); }
                Fail(machine.Item);
                return;
            }
            Release();
        }

        private void GoTo(Item item)
        {
            routeDestination = item;
            TryAddSubObjective(ref route, () => new AIObjectiveGoTo(item, character, objectiveManager,
                repeat: false, getDivingGearIfNeeded: false, closeEnough: AIObjectiveGetItem.DefaultReach)
            {
                AllowGoingOutside = false,
                SpeakIfFails = false,
                endNodeFilter = AIObjectiveGetItem.CreateEndNodeFilter(item)
            });
        }

        private IEnumerable<Deconstructor> GetMachines() =>
            (character.Submarine?.GetItems(alsoFromConnectedSubs: false) ?? Enumerable.Empty<Item>())
                .Select(item => item.GetComponent<Deconstructor>())
                .Where(device => device != null && !rejected.Contains(device.Item) && CanAccessMachine(device));

        private (Item, Deconstructor)? FindWork(Item[] candidates, Deconstructor[] machines)
        {
            var ready = machines.Where(device => !IsClaimed(device.Item) && IsReady(device)).ToArray();
            if (ready.Length == 0) { return null; }
            foreach (var item in candidates.Where(item => !IsClaimed(item))
                .OrderByDescending(item => item.IsOwnedBy(character))
                .ThenBy(item => Vector2.DistanceSquared(character.WorldPosition, Root(item).WorldPosition))
                .ThenBy(item => item.ID))
            {
                var device = ready.Where(device => CanUse(device, item))
                    .OrderBy(device => Vector2.DistanceSquared(Root(item).WorldPosition, device.Item.WorldPosition))
                    .ThenBy(device => device.Item.ID).FirstOrDefault();
                if (device != null) { return (item, device); }
            }
            return null;
        }

        private bool CanFetch(Item item)
        {
            if (item.Removed || item.Condition <= 0 || item.OrderedToBeIgnored || !item.AllowDeconstruct ||
                !IsAutomaticTarget(item.Prefab) || item.Illegitimate ||
                item.GetComponent<Pickable>() is not { CanBePicked: true, IsAttached: false } ||
                item.GetRootInventoryOwner() is Character owner && owner != character)
            {
                return false;
            }

            bool owned = item.IsOwnedBy(character);
            var root = Root(item);
            if (!owned && (root.Submarine == null || root.Submarine != character.Submarine || root.CurrentHull == null))
            {
                return false;
            }

            if (!item.HasAccess(character)) { return false; }

            for (var child = item; child.ParentInventory is ItemInventory inventory; child = inventory.Container.Item)
            {
                var container = inventory.Container;
                var cabinet = container.Item;
                if (inventory.Locked || !container.IsAccessible() || !cabinet.HasAccess(character) ||
                    !container.HasRequiredItems(character, addMessage: false) || cabinet.HasTag(Tags.DontTakeItems) ||
                    cabinet.GetComponent<Deconstructor>() != null)
                {
                    return false;
                }
            }

            return owned || Safe(root.CurrentHull) && FindCarrySlot(item) >= 0;
        }

        private int FindCarrySlot(Item item)
        {
            var inventory = character.Inventory;
            if (inventory.Locked) { return -1; }
            for (int i = 0; i < inventory.Capacity; i++)
            {
                if (inventory.SlotTypes[i] == InvSlotType.Any && inventory.CanBePutInSlot(item, i)) { return i; }
            }
            return -1;
        }

        private bool CanAccessMachine(Deconstructor device) =>
            !device.Item.Removed && device.Item.Submarine == character.Submarine && device.Item.ParentInventory == null &&
            Safe(device.Item.CurrentHull) && device.Item.HasAccess(character) &&
            device.HasRequiredItems(character, addMessage: false) &&
            device.InputContainer is { } input && input.IsAccessible() && !input.Inventory.Locked &&
            input.HasRequiredItems(character, addMessage: false) && device.OutputContainer != null;

        private bool IsReady(Deconstructor device) =>
            CanAccessMachine(device) && device.Item.Condition > 0 && device.HasPower && !device.IsActive &&
            !device.InputContainer.Inventory.AllItems.Any() && !device.OutputContainer.Inventory.Locked &&
            !device.OutputContainer.Inventory.IsFull(takeStacksIntoAccount: true);

        private static bool SupportsRecipe(Deconstructor device, Item item)
        {
            float condition = item.Condition / item.MaxCondition;
            var recipes = item.Prefab.DeconstructItems.Where(recipe =>
                recipe.RequiredOtherItem.Length == 0 && recipe.Amount > 0 &&
                recipe.IsValidDeconstructor(device.Item) &&
                condition >= recipe.MinCondition && condition <= recipe.MaxCondition);
            if (!recipes.Any() || recipes.Any(recipe => recipe.ItemIdentifier == item.Prefab.Identifier)) { return false; }

            var input = device.InputContainer.Inventory;
            return Enumerable.Range(0, input.Capacity).Any(slot =>
                input.HowManyCanBePut(item.Prefab, slot, item.Condition, ignoreItemsInSlot: true) > 0);
        }

        private bool CanUse(Deconstructor device, Item item) => IsReady(device) &&
            device.InputContainer.Inventory.CanBePut(item) && SupportsRecipe(device, item);
        private Item Destination => target.IsOwnedBy(character) ? machine.Item : Root(target);
        private static Item Root(Item item) => item.GetRootInventoryOwner() as Item ?? item;
        private bool CanReach(Item item) => character.CanInteractWith(item, checkLinked: false);

        private bool IsClaimed(Item item) => Claims.TryGetValue(item, out var claim) &&
            claim.TryGetTarget(out var other) && other != this && other.IsOrdered &&
            other.CanWork && other.objectiveManager.CurrentObjective == other &&
            (other.target == item || other.machine?.Item == item);

        private void Claim(Item item)
        {
            Claims.Remove(item);
            Claims.Add(item, new WeakReference<CrewDeconstructionObjective>(this));
        }

        private void Fail(Item item)
        {
            if (!item.Removed) { rejected.Add(item); }
            Release();
        }

        private void Unclaim(Item item)
        {
            if (item != null && Claims.TryGetValue(item, out var claim) &&
                claim.TryGetTarget(out var owner) && owner == this)
            {
                Claims.Remove(item);
            }
        }

        private void ClearRoute()
        {
            RemoveSubObjective(ref route);
            routeDestination = null;
        }

        private void Release()
        {
            Unclaim(target);
            Unclaim(machine?.Item);
            ClearRoute();
            if (collectedTarget && target is { Removed: false } &&
                target.ParentInventory == character.Inventory && character.CurrentHull != null)
            {
                target.Drop(character);
            }
            collectedTarget = false;
            target = null;
            machine = null;
        }
    }
}
