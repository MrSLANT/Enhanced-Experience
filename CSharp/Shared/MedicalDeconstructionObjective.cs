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
    internal abstract class AutomaticDeconstructionObjective : AIObjectiveDeconstructItems
    {
        protected abstract bool IsAutomaticTarget(ItemPrefab prefab);
        protected virtual bool DismissWhenEmpty => true;
        protected virtual bool ReleaseCollectedItem => false;

        private static readonly ConditionalWeakTable<Item, WeakReference<AutomaticDeconstructionObjective>>
            Claims = new ConditionalWeakTable<Item, WeakReference<AutomaticDeconstructionObjective>>();

        private readonly Dictionary<Item, double> retryAfter = new Dictionary<Item, double>();
        private (Item Item, Deconstructor Machine)? proposal;
        private Item target;
        private Deconstructor machine;
        private AIObjectiveGoTo route;
        private Item routeDestination;
        private bool collectedTarget;
        private double nextSearch;
        private double deadline;

        protected AutomaticDeconstructionObjective(Character character, AIObjectiveManager manager, float priorityModifier)
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
            if (!CanWork || Targets.Count > 0)
            {
                Release();
                proposal = null;
                return;
            }

            if (target != null)
            {
                if (Item.DeconstructItems.Contains(target) || !CanFetch(target) ||
                    ReleaseCollectedItem && IsClaimed(target))
                {
                    Release();
                }
                else if (ReleaseCollectedItem ? IsClaimed(machine.Item) || !IsReady(machine) : !CanUse(machine, target))
                {
                    Fail(machine.Item);
                }
                else if (route != null && (routeDestination != Destination || CanReach(Destination)))
                {
                    ClearRoute();
                }
                else if (now >= deadline || route?.Abandon == true)
                {
                    Fail(ReleaseCollectedItem ? Destination : target.IsOwnedBy(character) ? machine.Item : target);
                }
                return;
            }

            if (now < nextSearch) { return; }
            nextSearch = now + 2;
            foreach (var entry in retryAfter.Where(entry => entry.Key.Removed || entry.Value <= now).ToArray())
            {
                retryAfter.Remove(entry.Key);
            }
            proposal = FindWork();
            if (DismissWhenEmpty && proposal == null && !SubObjectives.Any() && !GetList().Any(IsValidTarget) &&
                !GetIngredients().Any(item => CanFetch(item, requireCarrySlot: false)))
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

            if (ReleaseCollectedItem && (!CanFetch(target) || Item.DeconstructItems.Contains(target) ||
                IsClaimed(target) || IsClaimed(machine.Item)))
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
                    Fail(ReleaseCollectedItem ? Destination : target.IsOwnedBy(character) ? machine.Item : target);
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
            }
            else if (ReleaseCollectedItem)
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

        private (Item, Deconstructor)? FindWork()
        {
            var items = character.Submarine?.GetItems(alsoFromConnectedSubs: false);
            if (items == null) { return null; }
            var machines = items.Select(item => item.GetComponent<Deconstructor>())
                .Where(device => device != null && !retryAfter.ContainsKey(device.Item) && !IsClaimed(device.Item) &&
                    IsReady(device)).ToArray();
            if (machines.Length == 0) { return null; }

            foreach (var item in GetIngredients()
                .Distinct().Where(item => !Item.DeconstructItems.Contains(item) && !retryAfter.ContainsKey(item) &&
                    !IsClaimed(item) && CanFetch(item) &&
                    (!ReleaseCollectedItem || item.IsOwnedBy(character) || !retryAfter.ContainsKey(Root(item))))
                .OrderByDescending(item => item.IsOwnedBy(character))
                .ThenBy(item => Vector2.DistanceSquared(character.WorldPosition, Root(item).WorldPosition))
                .ThenBy(item => item.ID))
            {
                var device = machines.Where(device => FitsRecipe(device, item))
                    .OrderBy(device => Vector2.DistanceSquared(Root(item).WorldPosition, device.Item.WorldPosition))
                    .ThenBy(device => device.Item.ID).FirstOrDefault();
                if (device != null) { return (item, device); }
            }
            return null;
        }

        private IEnumerable<Item> GetIngredients() =>
            (character.Submarine?.GetItems(alsoFromConnectedSubs: false) ?? Enumerable.Empty<Item>())
                .Concat(character.Inventory.FindAllItems(recursive: true));

        private bool CanFetch(Item item, bool requireCarrySlot = true)
        {
            if (item.Removed || item.Condition <= 0 || item.OrderedToBeIgnored || !item.AllowDeconstruct ||
                !IsAutomaticTarget(item.Prefab) || item.Illegitimate ||
                item.GetComponent<Pickable>() is not { CanBePicked: true, IsAttached: false } ||
                item.GetRootInventoryOwner() is Character owner && owner != character)
            {
                return false;
            }

            // The two vanilla eggs carry ignorebyai. Only this order may collect those
            // exact prefabs, while explicit player ignore marks still take precedence.
            bool egg = item.Prefab.Identifier.Value is "smallmudraptoregg" or "smallcrawleregg";
            if (egg ? !item.IsInteractable(character) : !item.HasAccess(character)) { return false; }

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

            if (item.IsOwnedBy(character)) { return true; }
            var root = Root(item);
            return root.Submarine == character.Submarine && root.CurrentHull != null &&
                (!requireCarrySlot || Safe(root.CurrentHull) && FindCarrySlot(item) >= 0);
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

        protected virtual bool IsReady(Deconstructor device) =>
            !device.Item.Removed && device.Item.Condition > 0 && device.Item.Submarine == character.Submarine &&
            device.Item.ParentInventory == null && Safe(device.Item.CurrentHull) && device.Item.HasAccess(character) &&
            device.HasRequiredItems(character, addMessage: false) && device.HasPower && !device.IsActive &&
            device.InputContainer is { } input && input.IsAccessible() && !input.Inventory.Locked &&
            input.HasRequiredItems(character, addMessage: false) && !input.Inventory.AllItems.Any() &&
            device.OutputContainer is { } output && !output.Inventory.IsFull(takeStacksIntoAccount: true);

        protected virtual bool FitsRecipe(Deconstructor device, Item item)
        {
            float condition = item.Condition / item.MaxCondition;
            return device.InputContainer.Inventory.CanBePut(item) && item.Prefab.DeconstructItems.Any(recipe =>
                recipe.RequiredOtherItem.Length == 0 && recipe.IsValidDeconstructor(device.Item) &&
                condition >= recipe.MinCondition && condition <= recipe.MaxCondition);
        }

        private bool CanUse(Deconstructor device, Item item) => IsReady(device) && FitsRecipe(device, item);
        private Item Destination => target.IsOwnedBy(character) ? machine.Item : Root(target);
        private static Item Root(Item item) => item.GetRootInventoryOwner() as Item ?? item;
        private bool CanReach(Item item) => character.CanInteractWith(item, checkLinked: false);

        private bool IsClaimed(Item item) => Claims.TryGetValue(item, out var claim) &&
            claim.TryGetTarget(out var other) && other != this && other.IsOrdered &&
            (!other.ReleaseCollectedItem || other.CanWork && other.objectiveManager.CurrentObjective == other) &&
            (other.target == item || other.machine?.Item == item);

        private void Claim(Item item)
        {
            Claims.Remove(item);
            Claims.Add(item, new WeakReference<AutomaticDeconstructionObjective>(this));
        }

        private void Fail(Item item)
        {
            if (!item.Removed) { retryAfter[item] = Timing.TotalTimeUnpaused + 20; }
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
            if (ReleaseCollectedItem && collectedTarget && target is { Removed: false } &&
                target.ParentInventory == character.Inventory && character.CurrentHull != null)
            {
                target.Drop(character);
            }
            collectedTarget = false;
            target = null;
            machine = null;
        }
    }

    internal sealed class MedicalDeconstructionObjective : AutomaticDeconstructionObjective
    {
        private static readonly HashSet<Identifier> Allowed = new HashSet<Identifier>(new[]
        {
            "aquaticpoppy", "yeastshroom", "slimebacteria", "adrenalinegland", "swimbladder",
            "smallmudraptoregg", "mucusball", "hallucinogenicbufotoxin", "oxygeniteshard",
            "smallcrawleregg", "sulphuriteshard", "divingknifeunique"
        }.Select(id => id.ToIdentifier()));

        public MedicalDeconstructionObjective(Character character, AIObjectiveManager manager, float priorityModifier)
            : base(character, manager, priorityModifier) { }

        internal static bool IsAutomaticIngredient(Identifier id) => Allowed.Contains(id);

        protected override bool IsAutomaticTarget(ItemPrefab prefab) => IsAutomaticIngredient(prefab.Identifier);
    }
}
