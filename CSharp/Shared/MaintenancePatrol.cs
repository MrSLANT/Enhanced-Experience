using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace EnhancedExperience
{
    internal sealed class MaintenancePatrol : AIObjective
    {
        private static readonly Identifier Cell = "mobilebattery".ToIdentifier();
        private static readonly Identifier Oxygen = "oxygensource".ToIdentifier();
        private static readonly Identifier RefillableOxygen = "refillableoxygensource".ToIdentifier();
        private static readonly Identifier Welding = "weldingfuel".ToIdentifier();
        private static readonly Identifier ReactorFuel = "reactorfuel".ToIdentifier();
        private static readonly Identifier AmmoBox = "ammobox".ToIdentifier();
        private static readonly Identifier Crate = "crate".ToIdentifier();
        private static readonly Identifier CellCharger = "batterycellrecharger".ToIdentifier();
        private static readonly Identifier TankRefiller = "oxygentankrefiller".ToIdentifier();

        private sealed record Service(Item Item, ItemContainer Origin, ItemContainer Station,
            Item Spare = null, PowerContainer Battery = null);

        private enum Stage { Start, CollectSpare, Load, Charging, Return, Recover, TuneBattery }

        private readonly Dictionary<Item, double> retryAfter = new();
        private double nextScan;
        private double deadline;
        private float lastCharge;
        private Service service;
        private Stage stage;
        private AIObjective step;

        public override Identifier Identifier { get; set; } = "maintenancepatrol".ToIdentifier();
        public override bool AbandonWhenCannotCompleteSubObjectives => false;

        internal MaintenancePatrol(Character character, AIObjectiveManager manager)
            : base(character, manager, 1f, Identifier.Empty) { }

        internal static void InstallForCrew(AIObjectiveManager manager, Character crew, MaintenancePatrol previous)
        {
            if (!CrewRepairPriorities.AppliesTo(crew))
            {
                return;
            }

            var job = crew.Info?.Job?.Prefab.Identifier.Value;
            if (job == "assistant")
            {
                if (manager.GetObjective<MaintenancePatrol>() == null)
                {
                    manager.AddObjective(previous ?? new MaintenancePatrol(crew, manager));
                }
            }
        }

        internal static bool NeedsService(float percent) => percent >= 0f && percent <= 90f;

        protected override bool CheckObjectiveState() => false;

        protected override float GetPriority()
        {
            if (!IsAllowed || character.CurrentHull == null || !character.IsOnPlayerTeam ||
                objectiveManager.ForcedOrder != null || objectiveManager.CurrentOrder?.Priority > 0f)
            {
                return 0f;
            }

            if (service == null && Timing.TotalTimeUnpaused >= nextScan)
            {
                nextScan = Timing.TotalTimeUnpaused + 3d;
                service = FindService();
                stage = Stage.Start;
                deadline = Timing.TotalTimeUnpaused + 45d;
            }

            // Let the bot do other work while the station charges the item.
            if (service == null || stage == Stage.Charging && !service.Item.IsFullCondition &&
                Timing.TotalTimeUnpaused < deadline) { return 0f; }
            return 30f;
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            if (service == null) { return; }

            var now = Timing.TotalTimeUnpaused;
            if (service.Item.Removed || stage == Stage.Charging &&
                (service.Item.ParentInventory != service.Station.Inventory || !CanAccess(service.Item)))
            {
                Finish(retry: true);
                return;
            }

            if (stage == Stage.Charging && service.Item.Condition > lastCharge)
            {
                lastCharge = service.Item.Condition;
                deadline = now + 30d;
            }

            if (objectiveManager.ForcedOrder != null || objectiveManager.CurrentOrder?.Priority > 0f)
            {
                if (stage != Stage.Charging) { deadline += deltaTime; }
                return;
            }

            if (stage == Stage.Charging) { return; }
            if (stage == Stage.TuneBattery && (!NeedsService(service.Battery.ChargePercentage) ||
                service.Battery.RechargeRatio > 0f))
            {
                Finish(retry: false);
                return;
            }
            if (step?.Abandon == true || now >= deadline || !CanAccess(service.Item) ||
                stage == Stage.CollectSpare && !CanAccess(service.Spare) ||
                stage == Stage.Load && service.Station != null && !CanAccess(service.Station.Item))
            {
                if (stage == Stage.Load && service.Origin != null && service.Item.IsOwnedBy(character))
                {
                    Return(service.Item, recovering: true);
                }
                else
                {
                    Finish(retry: true);
                }
            }
        }

        protected override void Act(float deltaTime)
        {
            if (service == null) { return; }

            RemoveSubObjective(ref step);
            var item = service.Item;
            switch (stage)
            {
                case Stage.Start:
                    if (service.Battery is { } battery)
                    {
                        if (!NeedsService(battery.ChargePercentage) || battery.RechargeRatio > 0f)
                        {
                            Finish(retry: false);
                            break;
                        }
                        BeginStep(Stage.TuneBattery, new AIObjectiveOperateItem(battery, character, objectiveManager,
                            "charge".ToIdentifier(), false, null)
                        {
                            Override = true,
                            completionCondition = () => battery.RechargeRatio > 0f
                        });
                    }
                    else if (!NeedsService(item.ConditionPercentage))
                    {
                        Finish(retry: false);
                    }
                    else if (service.Spare != null && service.Origin != null)
                    {
                        BeginStep(Stage.CollectSpare, new AIObjectiveGetItem(character, service.Spare,
                            objectiveManager, equip: false) { SpeakIfFails = false, AllowToFindDivingGear = false });
                    }
                    else
                    {
                        Load();
                    }
                    break;

                case Stage.CollectSpare:
                    if (service.Spare.IsOwnedBy(character) && service.Spare.IsFullCondition &&
                        NeedsService(item.ConditionPercentage)) { Load(); }
                    else { Finish(retry: true); }
                    break;

                case Stage.Load:
                    if (service.Station == null)
                    {
                        if (item.ParentInventory == null) { Return(service.Spare); }
                        else { Finish(retry: true); }
                    }
                    else if (item.ParentInventory != service.Station.Inventory)
                    {
                        Finish(retry: true);
                    }
                    else if (service.Spare != null && service.Origin != null)
                    {
                        Return(service.Spare);
                    }
                    else if (service.Origin != null)
                    {
                        stage = Stage.Charging;
                        lastCharge = item.Condition;
                        deadline = Timing.TotalTimeUnpaused + 30d;
                    }
                    else
                    {
                        Finish(retry: false);
                    }
                    break;

                case Stage.Charging:
                    if (item.IsFullCondition || Timing.TotalTimeUnpaused >= deadline)
                    {
                        Return(item, recovering: !item.IsFullCondition);
                    }
                    break;

                case Stage.Return:
                    var returned = service.Spare ?? item;
                    Finish(retry: returned.ParentInventory != service.Origin.Inventory);
                    break;

                case Stage.Recover:
                    Finish(retry: true);
                    break;

                case Stage.TuneBattery:
                    Finish(retry: service.Battery.RechargeRatio <= 0f);
                    break;
            }
        }

        private void Load()
        {
            var station = service.Station;
            var clearStationSlot = station != null && service.Origin == null && service.Spare != null;
            if (!CanAccess(service.Item) || station != null && !CanAccess(station.Item) ||
                station != null && !clearStationSlot && !station.Inventory.CanBePut(service.Item))
            {
                Finish(retry: true);
                return;
            }
            var move = Move(service.Item, station);
            if (clearStationSlot)
            {
                var spare = service.Spare;
                move.RemoveExistingWhenNecessary = true;
                move.RemoveExistingPredicate = candidate => candidate == spare && candidate.IsFullCondition;
                move.RemoveExistingMax = 1;
            }
            BeginStep(Stage.Load, move);
        }

        private void Return(Item item, bool recovering = false)
        {
            if (!CanAccess(service.Origin.Item) || !CanAccess(item) || !service.Origin.Inventory.CanBePut(item))
            {
                Finish(retry: true);
                return;
            }
            BeginStep(recovering ? Stage.Recover : Stage.Return, Move(item, service.Origin));
        }

        private AIObjectiveMoveItem Move(Item item, ItemContainer destination) =>
            new AIObjectiveMoveItem(character, item, objectiveManager,
                destination == null ? service.Origin : null, destination)
            {
                DropIfFails = false,
                AllowToFindDivingGear = false,
                AbandonGetItemDialogueCondition = () => false
            };

        private void BeginStep(Stage nextStage, AIObjective objective)
        {
            RemoveSubObjective(ref step);
            stage = nextStage;
            deadline = Timing.TotalTimeUnpaused + 45d;
            step = objective;
            AddSubObjective(step, false);
        }

        private void Finish(bool retry)
        {
            if (retry) { retryAfter[service.Item] = Timing.TotalTimeUnpaused + 60d; }
            RemoveSubObjective(ref step);
            service = null;
            nextScan = Timing.TotalTimeUnpaused + 1d;
        }

        private Service FindService()
        {
            var submarine = character.Submarine;
            if (submarine == null) { return null; }

            var now = Timing.TotalTimeUnpaused;
            foreach (var item in retryAfter.Where(entry => entry.Key.Removed || entry.Value <= now)
                .Select(entry => entry.Key).ToArray())
            {
                retryAfter.Remove(item);
            }

            var otherJobs = Character.CharacterList.Where(crew => crew != character && !crew.IsDead &&
                    crew.TeamID == character.TeamID && crew.AIController is HumanAIController)
                .Select(crew => ((HumanAIController)crew.AIController).ObjectiveManager.GetObjective<MaintenancePatrol>()?.service)
                .Where(job => job != null).ToArray();
            var reserved = new HashSet<Item>(otherJobs.SelectMany(job => new[] { job.Item, job.Spare }));
            var occupiedStations = new HashSet<ItemContainer>(otherJobs.Select(job => job.Station));
            var connected = new HashSet<Submarine>(submarine.GetConnectedSubs()) { submarine };
            var items = Item.ItemList.Where(item => connected.Contains(item.Submarine) ||
                item.IsOwnedBy(character)).ToArray();
            var stations = items.Where(item => item.Condition > 0f && item.ParentInventory == null &&
                    (item.HasTag(CellCharger) || item.HasTag(TankRefiller)) && CanAccess(item) &&
                    (item.GetComponent<Powered>() is not { } powered || powered.Voltage > 0f))
                .SelectMany(item => item.GetComponents<ItemContainer>())
                .Where(container => !occupiedStations.Contains(container) && CanUse(container)).ToArray();

            foreach (var item in items.Where(item => !reserved.Contains(item) && !retryAfter.ContainsKey(item) &&
                    (IsPortableResource(item) || item.GetComponent<PowerContainer>() != null))
                .OrderBy(item => Vector2.DistanceSquared(character.WorldPosition, item.WorldPosition)))
            {
                if (!CanAccess(item)) { continue; }
                if (item.GetComponent<PowerContainer>() is { } battery && item.ParentInventory == null &&
                    item.Condition > 0f && !battery.OutputDisabled && battery.MaxRechargeSpeed > 0f &&
                    battery.RechargeRatio <= 0f && NeedsService(battery.ChargePercentage))
                {
                    return new Service(item, null, null, Battery: battery);
                }

                if (!IsPortableResource(item) || !NeedsService(item.ConditionPercentage) ||
                    item.GetComponent<Pickable>() is not { IsAttached: false, CanBePicked: true }) { continue; }

                var origin = (item.ParentInventory as ItemInventory)?.Container;
                if (origin != null && IsStationFor(origin, item)) { continue; }

                foreach (var station in stations.Where(container => IsStationFor(container, item))
                    .OrderBy(container => Vector2.DistanceSquared(item.WorldPosition, container.Item.WorldPosition)))
                {
                    if (station.Inventory.CanBePut(item)) { return new Service(item, origin, station); }

                    var spare = station.Inventory.AllItems.FirstOrDefault(candidate => candidate.Prefab == item.Prefab &&
                        candidate.IsFullCondition && !reserved.Contains(candidate) && CanAccess(candidate));
                    if (spare != null) { return new Service(item, origin, station, spare); }
                }

                // Some resources, such as vanilla welding fuel, can only be replaced.
                if (origin == null || !IsEquipment(origin.Item)) { continue; }
                var replacement = items.FirstOrDefault(candidate => candidate.Prefab == item.Prefab &&
                    candidate.IsFullCondition && candidate.Container != origin.Item && !reserved.Contains(candidate) &&
                    (candidate.ParentInventory is not ItemInventory inventory ||
                        !IsEquipment(inventory.Container.Item) ||
                        IsStationFor(inventory.Container, candidate)) && CanAccess(candidate));
                if (replacement != null)
                {
                    var storage = (replacement.ParentInventory as ItemInventory)?.Container;
                    return new Service(item, origin, storage, replacement);
                }
            }

            return null;
        }

        private static bool IsPortableResource(Item item) => !item.HasTag(ReactorFuel) &&
            !item.HasTag(AmmoBox) && item.GetComponent<Projectile>() == null &&
            (item.HasTag(Cell) || item.HasTag(Oxygen) || item.HasTag(RefillableOxygen) || item.HasTag(Welding));

        private static bool IsEquipment(Item item) => !item.HasTag(Crate) &&
            (item.GetComponent<Holdable>() != null || item.GetComponent<Wearable>() != null);

        private static bool IsStationFor(ItemContainer container, Item item) =>
            item.HasTag(Cell) && container.Item.HasTag(CellCharger) ||
            container.Item.HasTag(TankRefiller) && (item.HasTag(RefillableOxygen) ||
                (item.HasTag(Oxygen) || item.HasTag(Welding)) &&
                container.ContainableItems.Concat(container.AllSubContainableItems).Any(rule =>
                    rule.MatchesItem(item.Prefab) && rule.StatusEffects?.Any(effect =>
                        effect.type == ActionType.OnContaining && effect.MatchesTagConditionals(item.Prefab) &&
                        effect.PropertyEffects.Any(property => property.Item1 == "condition" &&
                            property.Item2 is float amount && amount > 0f)) == true));

        private bool CanAccess(Item item)
        {
            if (item.Removed || item.IgnoreByAI(character) || item.IsClaimedByBallastFlora ||
                item.GetRootInventoryOwner() is Character owner && owner != character) { return false; }

            var root = item;
            while (root.ParentInventory is ItemInventory inventory)
            {
                if (!CanUse(inventory.Container)) { return false; }
                root = inventory.Container.Item;
            }

            var hull = root.IsOwnedBy(character) ? character.CurrentHull : root.CurrentHull;
            var sub = hull?.Submarine;
            return sub != null && sub.TeamID == character.TeamID && !sub.Info.IsOutpost &&
                character.Submarine != null && character.Submarine.IsConnectedTo(sub) &&
                !HumanAIController.UnsafeHulls.Contains(hull) && root.IsInteractable(character) &&
                root.HasAccess(character) && !root.IgnoreByAI(character);
        }

        private bool CanUse(ItemContainer container) => !container.Inventory.Locked &&
            !container.Item.HasTag(Tags.DontTakeItems) && !container.Item.IsClaimedByBallastFlora &&
            container.Item.IsInteractable(character) && container.Item.HasAccess(character) &&
            container.IsAccessible() && container.HasRequiredItems(character, addMessage: false);
    }
}
