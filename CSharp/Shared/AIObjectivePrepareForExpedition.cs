using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace EnhancedExperience
{
    internal sealed class AIObjectivePrepareForExpedition : AIObjective
    {
        private enum Readiness { Partial, Ready, PressureUnsafe }

        private static readonly string[] Suits = { "abyssdivingsuit", "pucs", "combatdivingsuit", "divingsuit" };
        private static readonly string[] Tanks = { "oxygenitetank", "oxygentank" };
        private static readonly (string[] Types, int Count)[] Supplies =
        {
            (Tanks, 2),
            (new[] { "antibleeding2", "antibleeding1" }, 2),
            (new[] { "antibloodloss2", "antibloodloss1" }, 1),
            (new[] { "antidama1" }, 1)
        };
        private static readonly string[] Tools = { "plasmacutter", "underwaterscooter" };

        private const double ScanInterval = 3;
        private const double RetryInterval = 30;
        private const double TravelTimeout = 25;
        // The vanilla diving-gear objective uses a 10% reserve when seeking oxygen inside a friendly sub.
        private const float MinReserveOxygen = 10;
        private const int SuitGroup = 0;
        private const int GroupCount = 7;

        private readonly Dictionary<Item, double> excluded = new Dictionary<Item, double>();
        private readonly double[] retryAt = new double[GroupCount];
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Item, WeakReference<AIObjectivePrepareForExpedition>>
            Reservations = new System.Runtime.CompilerServices.ConditionalWeakTable<Item, WeakReference<AIObjectivePrepareForExpedition>>();
        private AIObjectiveGoTo route;
        private Item target;
        private Item recipient;
        private bool equipSuit;
        private Func<bool> stillNeeded;
        private double deadline;
        private double nextCheck;
        private double nextInspection;
        private double nextScan;
        private int nextGroup;
        private bool? previousAbyss;
        private Submarine previousSub;
        private Readiness readiness;

        // The stock order supplies no planned depth. A mission integration can set this explicitly.
        private float? plannedDepth;
        public float? PlannedDepth
        {
            get => plannedDepth;
            set
            {
                if (plannedDepth == value) { return; }
                plannedDepth = value;
                retryAt[SuitGroup] = nextCheck = nextScan = 0;
            }
        }

        public override Identifier Identifier { get; set; } = "prepareforexpedition".ToIdentifier();
        public override string DebugTag => $"{Identifier} ({readiness})";
        public override bool KeepDivingGearOn => true;
        public override bool AllowAutomaticItemUnequipping => false;
        public override bool KeepDivingGearOnAlsoWhenInactive => true;
        public override bool AbandonWhenCannotCompleteSubObjectives => false;
        protected override bool AbandonIfDisallowed => false;
        protected override bool AllowWhileHandcuffed => false;

        public AIObjectivePrepareForExpedition(Character character, AIObjectiveManager manager, float priorityModifier)
            : base(character, manager, priorityModifier) { }

        protected override bool CheckObjectiveState() => false;

        protected override float GetPriority()
        {
            double now = Timing.TotalTime;
            if (!CanWork() || (target == null &&
                (now < Math.Max(nextCheck, nextScan) || !retryAt.Any(time => time <= now)))) { return 0; }
            return base.GetPriority();
        }

        public override void Update(float deltaTime)
        {
            double now = Timing.TotalTime;
            if (now >= nextInspection && character.Inventory != null)
            {
                nextInspection = now + 1;
                InspectKit();
            }
            if (target != null && (!CanWork() || !CanUse(target) ||
                (recipient != null && (!recipient.IsOwnedBy(character) || !CanUse(recipient))) ||
                (equipSuit && !CanChangeSuit())))
            {
                CancelAttempt(exclude: false);
            }
            else if (target != null && (Timing.TotalTime >= deadline || route?.Abandon == true))
            {
                CancelAttempt(exclude: true);
            }
            else if (route != null && CanReach(target))
            {
                RemoveSubObjective(ref route);
            }
            base.Update(deltaTime);
        }

        public override void Reset()
        {
            CancelAttempt(exclude: false);
            base.Reset();
        }

        public override void OnDeselected()
        {
            CancelAttempt(exclude: false);
            base.OnDeselected();
        }

        private bool SafeHull(Hull hull) =>
            hull != null && !hull.Removed && hull.FireSources.Count == 0 &&
            character.AIController is HumanAIController ai && !ai.UnsafeHulls.Contains(hull);

        private bool CanWork() => objectiveManager.IsOrder(this) && IsAllowed && SafeHull(character.CurrentHull) &&
            (character.IsProtectedFromPressure || character.CurrentHull.LethalPressure <= 0) &&
            !character.IsLowInOxygen && !character.IsIncapacitated;

        private bool CanChangeSuit() => CanWork() && !character.Inventory.Locked && !character.InWater &&
            character.CurrentHull.LethalPressure <= 0 &&
            !HumanAIController.NeedsDivingGear(character.CurrentHull, out _, objectiveManager);

        private bool AbyssAllowed()
        {
            var sub = character.Submarine;
            float? depth = sub == null ? null : Level.Loaded?.GetRealWorldDepth(sub.WorldPosition.Y);
            if (PlannedDepth > 6500) { return true; }
            if (!(depth >= 6500)) { return false; }

            double water = 0, volume = 0;
            foreach (var hull in Hull.HullList)
            {
                if (!hull.Removed && hull.Submarine == sub && hull.Volume > 0)
                {
                    water += hull.WaterVolume;
                    volume += hull.Volume;
                }
            }
            return ExpeditionPolicy.AllowAbyss(PlannedDepth, depth, ExpeditionPolicy.FloodingRatio(water, volume));
        }

        private static string Id(Item item) => item.Prefab.Identifier.Value;
        private static int SuitRank(Item item, bool abyss)
        {
            int rank = Array.IndexOf(Suits, Id(item));
            return rank == 0 && !abyss ? -1 : rank;
        }

        private static bool IsInstalled(Item item)
        {
            for (var parent = item.Container; parent != null; parent = parent.Container)
            {
                if ((parent.GetComponent<Wearable>() is Wearable wearable && wearable.PressureProtection > 0) ||
                    parent.HasTag("diving".ToIdentifier()) ||
                    (parent.HasTag("tool".ToIdentifier()) && !parent.HasTag("mobilecontainer".ToIdentifier())) ||
                    parent.GetComponent<RepairTool>() != null ||
                    parent.GetComponent<Propulsion>() != null || parent.GetComponent<RangedWeapon>() != null ||
                    parent.HasTag("batterycellrecharger".ToIdentifier()))
                {
                    return true;
                }
            }
            return false;
        }

        private bool CanUse(Item item)
        {
            if (item.Removed || item.Condition <= 0 || item.ParentInventory?.Locked == true || !item.HasAccess(character) ||
                !item.IsInteractable(character) || item.GetComponent<Pickable>() is not { CanBePicked: true }) { return false; }
            for (var child = item; child.ParentInventory is ItemInventory inventory; child = inventory.Container.Item)
            {
                var container = inventory.Container;
                if (inventory.Locked || !container.Item.HasAccess(character) ||
                    !container.Item.IsInteractable(character) || !container.HasRequiredItems(character, addMessage: false))
                {
                    return false;
                }
            }
            return true;
        }

        private bool CanTake(Item item)
        {
            if (!CanUse(item) || item.Illegitimate || IsInstalled(item) ||
                character.IsItemTakenBySomeoneElse(item)) { return false; }

            Entity root = item.GetRootInventoryOwner();
            if (root is Character) { return false; }
            var rootItem = root as Item ?? item;
            if (rootItem.Submarine != character.Submarine || !SafeHull(rootItem.CurrentHull)) { return false; }

            for (var parent = item.Container; parent != null; parent = parent.Container)
            {
                if (parent.HasTag(Tags.DontTakeItems)) { return false; }
            }
            return true;
        }

        private bool IsReserved(Item item) => Reservations.TryGetValue(item, out var claim) &&
            claim.TryGetTarget(out var other) && other != this && other.target == item &&
            other.objectiveManager.IsOrder(other);

        private IEnumerable<Item> Rank(IEnumerable<Item> candidates, Func<Item, int> type) =>
            candidates.Select(item => (Item: item, Type: type(item)))
                .Where(entry => entry.Type >= 0)
                .OrderBy(entry => ExpeditionPolicy.Rank(entry.Type, entry.Item.Quality, entry.Item.ConditionPercentage,
                    Vector2.DistanceSquared(character.WorldPosition,
                        (entry.Item.GetRootInventoryOwner() ?? entry.Item).WorldPosition), entry.Item.ID))
                .Select(entry => entry.Item);

        private (Item[] Owned, Item Suit, int SuitRank, bool Abyss, Item[] ToolItems, int[] Missing) InspectKit()
        {
            bool abyss = AbyssAllowed();
            if (previousSub != character.Submarine || previousAbyss != abyss)
            {
                retryAt[SuitGroup] = 0;
                nextScan = 0;
                previousSub = character.Submarine;
                previousAbyss = abyss;
            }

            var owned = character.Inventory.FindAllItems(CanUse, recursive: true).Distinct().ToArray();
            var suit = character.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes);
            var suitRank = suit == null || !CanUse(suit) ? -1 : SuitRank(suit, abyss);
            bool pressureSafe = suit != null && suitRank >= 0 &&
                AIObjectiveFindDivingGear.IsSuitablePressureProtection(suit, Tags.HeavyDivingGear, character) &&
                (PlannedDepth == null || suit.GetComponent<Wearable>()?.PressureProtection >= PlannedDepth + Steering.PressureWarningThreshold);

            var toolItems = Tools.Select(id => Rank(owned.Where(item => Id(item) == id),
                item => IsOperational(item) ? 0 : 1).FirstOrDefault()).ToArray();
            var missing = Supplies.Select(group => ExpeditionPolicy.Missing(group.Count,
                owned.Count(item => IsSupply(item, group.Types)))).ToArray();

            bool complete = suitRank >= 0 && HasSuitOxygen(suit) &&
                toolItems.All(IsOperational) && missing.All(count => count == 0);
            readiness = !pressureSafe ? Readiness.PressureUnsafe : complete ? Readiness.Ready : Readiness.Partial;

            return (owned, suit, suitRank, abyss, toolItems, missing);
        }

        protected override void Act(float deltaTime)
        {
            double now = Timing.TotalTime;
            if (target != null)
            {
                FinishAttempt();
                nextCheck = now + 0.5;
                return;
            }
            if (now < nextCheck || !CanWork()) { return; }
            nextCheck = now + 1;

            foreach (var entry in excluded.Where(entry => entry.Key.Removed || now >= entry.Value).ToArray())
            {
                excluded.Remove(entry.Key);
            }

            var (owned, suit, suitRank, abyss, toolItems, missing) = InspectKit();
            if (now < nextScan) { return; }
            List<Item> storage = null;
            IEnumerable<Item> Storage()
            {
                return storage ??= character.Submarine.GetItems(alsoFromConnectedSubs: false)
                    .Where(item => !excluded.ContainsKey(item) && !IsReserved(item) && CanTake(item)).ToList();
            }

            for (int offset = 0; offset < GroupCount; offset++)
            {
                int group = (nextGroup + offset) % GroupCount;
                if (now < retryAt[group]) { continue; }
                bool started;
                if (group == SuitGroup)
                {
                    started = PrepareSuit(owned, Storage, suit, suitRank, abyss);
                }
                else if (group <= Tools.Length)
                {
                    int tool = group - 1;
                    started = PrepareTool(owned, Storage, toolItems[tool], Tools[tool]);
                }
                else
                {
                    int supply = group - 1 - Tools.Length;
                    var types = Supplies[supply].Types;
                    started = missing[supply] > 0 && TryStart(Rank(Storage().Where(item => IsSupply(item, types)),
                        item => Array.IndexOf(types, Id(item))), null, needed: () =>
                        character.Inventory.FindAllItems(item => CanUse(item) && IsSupply(item, types), recursive: true)
                            .Distinct().Count() < Supplies[supply].Count);
                }

                if (started)
                {
                    nextGroup = (group + 1) % GroupCount;
                    break;
                }
                retryAt[group] = now + RetryInterval;
            }
            if (storage != null) { nextScan = now + ScanInterval; }
        }

        private static bool IsSupply(Item item, string[] types) =>
            Array.IndexOf(types, Id(item)) >= 0 && !IsInstalled(item) &&
            (types != Tanks || item.ConditionPercentage >= MinReserveOxygen);

        private bool PrepareSuit(Item[] owned, Func<IEnumerable<Item>> storage, Item worn, int wornRank, bool abyss)
        {
            if (CanChangeSuit() && wornRank != (abyss ? 0 : 1))
            {
                int RankSuit(Item item)
                {
                    int rank = SuitRank(item, abyss);
                    return wornRank < 0 || rank < wornRank ? rank : -1;
                }

                var candidates = owned.Where(item => item != worn && !excluded.ContainsKey(item));
                if (!candidates.Any(item => RankSuit(item) == (abyss ? 0 : 1) && CanPlace(item, null, equip: true)))
                {
                    candidates = candidates.Concat(storage());
                }
                if (TryStart(Rank(candidates, RankSuit), null, equip: true)) { return true; }
            }
            return wornRank >= 0 && !HasSuitOxygen(worn) &&
                PrepareCharge(owned, storage, worn, item => IsSupply(item, Tanks));
        }

        private static bool HasSuitOxygen(Item suit) =>
            suit?.ContainedItems.Any(item => item.HasTag(Tags.OxygenSource) && item.Condition > 0) == true;

        private ItemComponent ToolComponent(Item item) => Id(item) == Tools[0]
            ? item.GetComponent<RepairTool>() : item.GetComponent<Propulsion>();

        private bool IsOperational(Item item) =>
            item != null && ToolComponent(item)?.IsEmpty(character) == false;

        private bool PrepareTool(Item[] owned, Func<IEnumerable<Item>> storage, Item tool, string id)
        {
            if (IsOperational(tool)) { return false; }
            if (tool == null)
            {
                bool MissingTool() => character.Inventory.FindItem(item => Id(item) == id, recursive: true) == null;
                if (!MissingTool()) { return false; }

                return TryStart(Rank(storage().Where(item => Id(item) == id),
                    item => IsOperational(item) ? 0 : 1), null, needed: MissingTool);
            }

            return PrepareCharge(owned, storage, tool, item => !IsInstalled(item) && (!item.HasTag(Tags.OxygenSource) || item.ConditionPercentage >= MinReserveOxygen) &&
                ToolComponent(tool)?.RequiredItems.TryGetValue(RelatedItem.RelationType.Contained, out var requirements) == true &&
                requirements.Any(requirement => requirement.MatchesItem(item)));
        }

        private bool PrepareCharge(Item[] owned, Func<IEnumerable<Item>> storage, Item destination, Func<Item, bool> filter)
        {
            int ChargeRank(Item item) => Array.IndexOf(Tanks, Id(item)) == 1 ? 1 : 0;
            return TryStart(Rank(owned.Where(item => filter(item) && !excluded.ContainsKey(item)), ChargeRank), destination) ||
                TryStart(Rank(storage().Where(filter), ChargeRank), destination);
        }

        private (Inventory Inventory, int Slot)? StowSlot(Item item)
        {
            var inventory = character.Inventory;
            if (inventory.Locked) { return null; }
            for (int i = 0; i < inventory.Capacity; i++)
            {
                if (inventory.SlotTypes[i] == InvSlotType.Any && !inventory.IsInSlot(item, i) &&
                    inventory.CanBePutInSlot(item, i))
                {
                    return (inventory, i);
                }
            }
            foreach (var bag in inventory.AllItems)
            {
                if (!bag.HasTag("mobilecontainer".ToIdentifier()) || IsInstalled(bag) || !CanUse(bag) || bag.OwnInventory is not { Locked: false } pockets ||
                    pockets.ItemOwnsSelf(item))
                {
                    continue;
                }
                for (int i = 0; i < pockets.Capacity; i++)
                {
                    if (!pockets.IsInSlot(item, i) && pockets.CanBePutInSlot(item, i)) { return (pockets, i); }
                }
            }
            return null;
        }

        private int DestinationSlot(Item item, Item destination, bool equip, out Item displaced)
        {
            displaced = null;
            if (equip)
            {
                if (!CanChangeSuit() || item.GetComponent<Wearable>() == null) { return -1; }
                displaced = character.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes);
                return character.Inventory.FindLimbSlot(InvSlotType.OuterClothes);
            }

            var container = destination.GetComponent<ItemContainer>();
            if (container == null || container.Inventory.Locked ||
                !container.HasRequiredItems(character, addMessage: false) || !container.ShouldBeContained(item, out _)) { return -1; }
            for (int i = 0; i < container.Inventory.Capacity; i++)
            {
                if (container.Inventory.CanBePutInSlot(item, i)) { return i; }
            }

            // An empty single-slot consumable can be preserved in personal inventory.
            if (container.Inventory.Capacity != 1) { return -1; }
            displaced = container.Inventory.GetItemAt(0);
            return displaced is { Condition: <= 0 } ? 0 : -1;
        }

        private bool CanPlace(Item item, Item destination, bool equip)
        {
            if (destination == null && !equip) { return StowSlot(item).HasValue; }
            return DestinationSlot(item, destination, equip, out var displaced) >= 0 &&
                (displaced == null || StowSlot(displaced).HasValue);
        }

        private bool TryStart(IEnumerable<Item> candidates, Item destination, bool equip = false, Func<bool> needed = null)
        {
            foreach (var item in candidates)
            {
                if (!CanPlace(item, destination, equip)) { continue; }
                target = item;
                recipient = destination;
                equipSuit = equip;
                stillNeeded = needed;
                deadline = Timing.TotalTime + TravelTimeout;
                Reservations.Remove(item);
                Reservations.Add(item, new WeakReference<AIObjectivePrepareForExpedition>(this));

                if (CanReach(item))
                {
                    FinishAttempt();
                }
                else
                {
                    var root = item.GetRootInventoryOwner() as Item ?? item;
                    TryAddSubObjective(ref route, () => new AIObjectiveGoTo(root, character, objectiveManager,
                        repeat: false, getDivingGearIfNeeded: false, closeEnough: AIObjectiveGetItem.DefaultReach)
                    {
                        AllowGoingOutside = false,
                        SpeakIfFails = false,
                        endNodeFilter = AIObjectiveGetItem.CreateEndNodeFilter(root),
                        AbortCondition = _ => target != item || !CanTake(item) ||
                            (item.GetRootInventoryOwner() as Item ?? item) != root
                    });
                }
                return true;
            }
            return false;
        }

        private bool CanReach(Item item) => item.IsOwnedBy(character) ||
            character.CanInteractWith(item.GetRootInventoryOwner() as Item ?? item, checkLinked: false);

        private bool Put(Item item, Inventory inventory, int slot) =>
            inventory.TryPutItem(item, slot, allowSwapping: false, allowCombine: false, user: character);

        private bool Transfer(Item item, Item destination, bool equip)
        {
            if (destination == null && !equip)
            {
                return StowSlot(item) is { } stow && Put(item, stow.Inventory, stow.Slot);
            }
            int slot = DestinationSlot(item, destination, equip, out var displaced);
            if (slot < 0) { return false; }
            Inventory inventory = equip ? character.Inventory : destination.GetComponent<ItemContainer>().Inventory;
            if (displaced == null) { return Put(item, inventory, slot); }

            if (StowSlot(displaced) is not { } spare || !Put(displaced, spare.Inventory, spare.Slot)) { return false; }
            if (Put(item, inventory, slot)) { return true; }
            Put(displaced, inventory, slot);
            return false;
        }

        private bool ShouldEquip(Item item)
        {
            bool abyss = AbyssAllowed();
            int rank = SuitRank(item, abyss);
            var worn = character.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes);
            int wornRank = worn == null || !CanUse(worn) ? -1 : SuitRank(worn, abyss);
            return rank >= 0 && (wornRank < 0 || rank < wornRank);
        }

        private void FinishAttempt()
        {
            if (stillNeeded?.Invoke() == false)
            {
                CancelAttempt(exclude: false);
                return;
            }
            bool success = CanWork() && CanUse(target) && CanReach(target) &&
                (target.IsOwnedBy(character) || CanTake(target)) &&
                (recipient == null || recipient.IsOwnedBy(character) && CanUse(recipient)) &&
                (!equipSuit || ShouldEquip(target)) &&
                Transfer(target, recipient, equipSuit);
            CancelAttempt(exclude: !success);
        }

        private void CancelAttempt(bool exclude)
        {
            if (target != null)
            {
                if (exclude && !target.Removed) { excluded[target] = Timing.TotalTime + RetryInterval; }
                if (Reservations.TryGetValue(target, out var claim) && claim.TryGetTarget(out var owner) &&
                    owner == this)
                {
                    Reservations.Remove(target);
                }
            }
            RemoveSubObjective(ref route);
            target = null;
            recipient = null;
            stillNeeded = null;
        }
    }
}
