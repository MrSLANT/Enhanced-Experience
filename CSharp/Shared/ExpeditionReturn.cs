using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace EnhancedExperience
{
    internal sealed class ExpeditionInventory
    {
        private static readonly ConditionalWeakTable<Character, ExpeditionInventory> Expeditions = new();

        private HashSet<Item> baseline;
        private Submarine home;
        internal readonly Dictionary<Item, ItemInventory> Origins = new();
        internal readonly HashSet<Item> Loot = new();

        internal static ExpeditionInventory Get(Character bot) =>
            Expeditions.TryGetValue(bot, out var expedition) ? expedition : null;

        internal static void Begin(Character bot)
        {
            var home = bot.Submarine ?? Submarine.MainSubs.FirstOrDefault(sub => sub?.TeamID == bot.TeamID);
            if (home == null || bot.Inventory == null) { return; }
            var expedition = Expeditions.GetOrCreateValue(bot);
            if (expedition.baseline != null) { return; }
            expedition.baseline = Carried(bot).ToHashSet();
            expedition.home = home;
        }

        internal static IEnumerable<Item> Carried(Character bot) =>
            bot.Inventory.FindAllItems(item => !item.Removed, recursive: true).Distinct();

        internal static IEnumerable<Item> Contents(Item item) => item.OwnInventory == null
            ? Enumerable.Empty<Item>()
            : item.OwnInventory.FindAllItems(child => !child.Removed, recursive: true).Distinct();

        internal static void RecordPickup(Character bot, Item item, ItemInventory source)
        {
            var expedition = Get(bot);
            if (expedition == null || !item.IsOwnedBy(bot) || expedition.baseline?.Contains(item) == true) { return; }

            if (source == null)
            {
                // Loose equipment has no return destination.
                expedition.baseline.Add(item);
                expedition.baseline.UnionWith(Contents(item));
                expedition.Loot.Remove(item);
                expedition.Loot.ExceptWith(Contents(item));
                return;
            }

            expedition.Origins.TryAdd(item, source);
            expedition.Loot.Remove(item);
            foreach (var child in Contents(item))
            {
                if (child.ParentInventory is ItemInventory parent && !expedition.baseline.Contains(child))
                {
                    expedition.Origins.TryAdd(child, parent);
                    expedition.Loot.Remove(child);
                }
            }
        }

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Inventory), "PutItem", new[]
            {
                typeof(Item), typeof(int), typeof(Character), typeof(bool), typeof(bool), typeof(bool)
            }), prefix: new HarmonyMethod(typeof(ExpeditionInventory), nameof(BeforePut)),
                postfix: new HarmonyMethod(typeof(ExpeditionInventory), nameof(AfterPut)));
        }

        private static void BeforePut(Item item, out Entity __state) => __state = item?.GetRootInventoryOwner();

        private static void AfterPut(Item item, Entity __state)
        {
            if (GameMain.NetworkMember?.IsClient == true ||
                item?.GetRootInventoryOwner() is not Character bot || __state == bot ||
                Get(bot) is not { baseline: not null, home: not null } expedition ||
                (bot.CurrentHull != null && bot.Submarine?.IsConnectedTo(expedition.home) == true)) { return; }

            foreach (var acquired in Contents(item).Prepend(item))
            {
                if (!expedition.baseline.Contains(acquired) && !expedition.Origins.ContainsKey(acquired))
                {
                    expedition.Loot.Add(acquired);
                }
            }
        }

        internal void Finish(Character bot)
        {
            baseline = null;
            home = null;
            Loot.RemoveWhere(item => item.Removed || !item.IsOwnedBy(bot));
            foreach (var item in Origins.Keys.Where(item => item.Removed || !item.IsOwnedBy(bot)).ToArray())
            {
                Origins.Remove(item);
            }
            if (Loot.Count == 0 && Origins.Count == 0) { Expeditions.Remove(bot); }
        }

        internal static void Clear() => Expeditions.Clear();
    }

    internal sealed class ExpeditionReturn : AIObjectiveReturn
    {
        private const double TravelTimeout = 25;
        private const int LargeLockerCapacity = 35;
        private static readonly Identifier Locker = "locker".ToIdentifier();
        private static readonly Identifier Diving = "diving".ToIdentifier();

        private readonly ExpeditionInventory expedition;
        private readonly Queue<Item> items = new();
        private readonly Queue<Item> deferredGear = new();
        private readonly HashSet<ItemInventory> touched = new();
        private readonly HashSet<ItemInventory> rejected = new();
        private ItemInventory[] lockers;
        private Queue<ItemInventory> sorting;
        private Item item;
        private ItemInventory destination;
        private AIObjectiveGoTo route;
        private bool initialized;
        private bool retryingNestedGear;
        private double deadline;

        internal ExpeditionReturn(Character bot, AIObjectiveManager manager, Order order, float priorityModifier,
            ExpeditionInventory expedition) : base(bot, order.OrderGiver, manager, priorityModifier)
        {
            this.expedition = expedition;
            Completed += () => ExpeditionPlugin.DismissOrder(manager, order);
        }

        // Preserve the stock travel objective and its failure handling before reaching the return target.
        public override bool AbandonWhenCannotCompleteSubObjectives =>
            character.Submarine != Target && destination == null;

        protected override bool CheckObjectiveState() => Target == null && base.CheckObjectiveState();

        public override void Reset()
        {
            base.Reset();
            route = null;
            destination = null;
        }

        public override void OnDeselected()
        {
            RemoveSubObjective(ref route);
            destination = null;
            base.OnDeselected();
        }

        public override void Update(float deltaTime)
        {
            if (route != null && (!OnHomeSubmarine(character.Submarine) || !CanAccess(destination) ||
                route.Abandon || Timing.TotalTimeUnpaused >= deadline))
            {
                FinishVisit(success: false);
            }
            base.Update(deltaTime);
        }

        protected override void Act(float deltaTime)
        {
            if (character.Submarine != Target && (!initialized || !OnHomeSubmarine(character.Submarine)))
            {
                base.Act(deltaTime);
                return;
            }

            if (!initialized)
            {
                foreach (var gear in expedition.Origins.Keys.Where(StillCarried))
                {
                    // Original contents travel with the borrowed container when still installed.
                    if (gear.ParentInventory != expedition.Origins[gear] ||
                        !expedition.Origins.ContainsKey(gear.Container)) { items.Enqueue(gear); }
                }
                foreach (var loot in expedition.Loot.Where(StillCarried)) { items.Enqueue(loot); }
                initialized = true;
            }

            if (sorting != null)
            {
                SortNext();
                return;
            }

            if (item == null)
            {
                if (!items.TryDequeue(out item))
                {
                    if (deferredGear.Count > 0)
                    {
                        retryingNestedGear = true;
                        while (deferredGear.TryDequeue(out var gear)) { items.Enqueue(gear); }
                        return;
                    }
                    sorting = new Queue<ItemInventory>(touched.OrderBy(inv => inv.Container.Item.ID));
                    return;
                }
                rejected.Clear();
            }
            MoveItem();
        }

        private bool StillCarried(Item target) => !target.Removed && target.IsOwnedBy(character);

        private bool OnHomeSubmarine(Submarine sub) =>
            sub != null && sub.TeamID == Target.TeamID && sub.IsConnectedTo(Target);

        private bool SafeHull(Hull hull) => hull != null && !hull.Removed && hull.FireSources.Count == 0 &&
            !HumanAIController.UnsafeHulls.Contains(hull);

        private bool CanAccess(ItemInventory inventory)
        {
            if (inventory == null) { return false; }
            Item owner = inventory.Container.Item;
            var rootOwner = owner.GetRootInventoryOwner();
            if (rootOwner is Character carrier && carrier != character) { return false; }
            Item root = rootOwner as Item ?? owner;
            if (!owner.IsOwnedBy(character) && (!OnHomeSubmarine(root.Submarine) || !SafeHull(root.CurrentHull)))
            {
                return false;
            }

            for (var container = inventory; container != null; container = container.Container.Item.ParentInventory as ItemInventory)
            {
                var component = container.Container;
                var storage = component.Item;
                if (storage.Removed || storage.Condition <= 0 || storage.IsClaimedByBallastFlora ||
                    !storage.IsInteractable(character) || !storage.HasAccess(character) ||
                    container.Locked || !component.IsAccessible() ||
                    !component.HasRequiredItems(character, addMessage: false)) { return false; }
            }
            return true;
        }

        private bool CanHandle(Item target)
        {
            if (!StillCarried(target) || character.Inventory.Locked || target.ParentInventory.Locked ||
                !target.HasAccess(character) || !target.IsInteractable(character)) { return false; }
            return target.ParentInventory is not ItemInventory nested || CanAccess(nested);
        }

        private bool CanRemoveDivingGear(Item target)
        {
            for (var worn = target; worn != null; worn = worn.Container)
            {
                if (worn == character.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes) ||
                    worn == character.Inventory.GetItemInLimbSlot(InvSlotType.Head) && worn.HasTag(Diving))
                {
                    return !character.InWater && !character.IsLowInOxygen &&
                        character.CurrentHull.LethalPressure <= 0 &&
                        !HumanAIController.NeedsDivingGear(character.CurrentHull, out _, objectiveManager);
                }
            }
            return true;
        }

        private int FindSlot(ItemInventory inventory)
        {
            if (!CanAccess(inventory) || inventory.ItemOwnsSelf(item)) { return -1; }
            for (int slot = 0; slot < inventory.Capacity; slot++)
            {
                if (inventory.CanBePutInSlot(item, slot)) { return slot; }
            }
            return -1;
        }

        private ItemInventory FindLocker()
        {
            lockers ??= Target.GetItems(alsoFromConnectedSubs: true)
                .Where(candidate => candidate.ParentInventory == null && candidate.HasTag(Locker) &&
                    !candidate.HasTag(Tags.DontTakeItems))
                .Select(candidate => candidate.OwnInventory)
                .Where(inventory => inventory != null && inventory.Capacity >= LargeLockerCapacity)
                .ToArray();

            return lockers.Where(inventory => !rejected.Contains(inventory) &&
                    inventory.Container.Item.ParentInventory == null && FindSlot(inventory) >= 0)
                .OrderByDescending(inventory => item.Prefab.PreferredContainers.Any(preference =>
                    preference.Primary.Contains(inventory.Container.Item.Prefab.Identifier)))
                .ThenBy(inventory => Vector2.DistanceSquared(character.WorldPosition, inventory.Container.Item.WorldPosition))
                .ThenBy(inventory => inventory.Container.Item.ID)
                .FirstOrDefault();
        }

        private void MoveItem()
        {
            bool borrowed = expedition.Origins.TryGetValue(item, out var origin);
            if (!CanHandle(item) || item.ParentInventory == origin || !SafeHull(character.CurrentHull))
            {
                FinishVisit(success: true);
                return;
            }

            var contents = ExpeditionInventory.Contents(item).ToArray();
            if (contents.Any(child => borrowed
                ? !expedition.Origins.TryGetValue(child, out var source) || child.ParentInventory != source
                : !expedition.Loot.Contains(child)))
            {
                if (borrowed && !retryingNestedGear) { deferredGear.Enqueue(item); }
                FinishVisit(success: true);
                return;
            }

            var next = destination ?? (borrowed ? origin : FindLocker());
            int slot = !borrowed && next?.Container.Item.ParentInventory != null ? -1 : FindSlot(next);
            if (slot < 0)
            {
                FinishVisit(success: next == null || borrowed);
                return;
            }
            if (!Reach(next)) { return; }
            if (!CanRemoveDivingGear(item))
            {
                FinishVisit(success: true);
                return;
            }

            var previous = item.ParentInventory as ItemInventory;
            bool moved = next.TryPutItem(item, slot, allowSwapping: false, allowCombine: false, user: character,
                createNetworkEvent: true, triggerOnInsertedEffects: false) && item.ParentInventory == next;
            if (moved)
            {
                TrackStorage(previous);
                TrackStorage(next);
            }
            if (!moved && !borrowed) { rejected.Add(next); }
            FinishVisit(success: moved);
        }

        private void TrackStorage(ItemInventory inventory)
        {
            if (inventory != null && (!inventory.Container.Item.IsOwnedBy(character) ||
                expedition.Origins.ContainsKey(inventory.Container.Item) || expedition.Loot.Contains(inventory.Container.Item)))
            {
                touched.Add(inventory);
            }
        }

        private void SortNext()
        {
            if (!sorting.TryPeek(out var storage))
            {
                expedition.Finish(character);
                IsCompleted = true;
                return;
            }
            if (!CanAccess(storage) || storage.Container.Item.IsOwnedBy(character) || !SafeHull(character.CurrentHull))
            {
                FinishVisit(success: false);
                return;
            }
            if (!Reach(storage)) { return; }
            MedicalCabinetOrganizer.Organize(storage.Container);
            FinishVisit(success: true);
        }

        private bool Reach(ItemInventory inventory)
        {
            var owner = inventory.Container.Item;
            Item root = owner.GetRootInventoryOwner() as Item ?? owner;
            if (owner.IsOwnedBy(character) || character.CanInteractWith(root, checkLinked: false)) { return true; }
            if (destination != null)
            {
                FinishVisit(success: false);
                return false;
            }
            destination = inventory;
            deadline = Timing.TotalTimeUnpaused + TravelTimeout;
            TryAddSubObjective(ref route, () => new AIObjectiveGoTo(root, character, objectiveManager,
                repeat: false, getDivingGearIfNeeded: false, closeEnough: AIObjectiveGetItem.DefaultReach)
            {
                AllowGoingOutside = false,
                SpeakIfFails = false,
                endNodeFilter = AIObjectiveGetItem.CreateEndNodeFilter(root)
            });
            return false;
        }

        private void FinishVisit(bool success)
        {
            if (sorting != null) { sorting.Dequeue(); }
            else if (success || expedition.Origins.ContainsKey(item)) { item = null; }
            else if (destination != null) { rejected.Add(destination); }
            RemoveSubObjective(ref route);
            destination = null;
        }
    }
}
