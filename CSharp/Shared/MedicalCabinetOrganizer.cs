using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;

namespace EnhancedExperience
{
    internal static class MedicalCabinetOrganizer
    {
        internal static void Organize(ItemContainer cabinet)
        {
            if (GameMain.NetworkMember?.IsClient == true || cabinet.Inventory.Locked) { return; }
            var inventory = cabinet.Inventory;
            var movedItems = new HashSet<Item>();

            // Match the button's back-to-front merge using the game's actual stack rules.
            for (int source = inventory.Capacity - 1; source > 0; source--)
            {
                foreach (var item in inventory.GetItemsAt(source).ToArray())
                {
                    for (int destination = 0; destination < source; destination++)
                    {
                        if (inventory.GetItemAt(destination) == null || !inventory.CanBePutInSlot(item, destination))
                        {
                            continue;
                        }
                        inventory.ForceRemoveFromSlot(item, source);
                        inventory.ForceToSlot(item, destination);
                        movedItems.Add(item);
                        break;
                    }
                }
            }

            // Keep whole stacks together. Swap only if both stacks fit in the new slots.
            // Membership never changes, so no item is dropped and no insertion effect runs.
            var slots = Enumerable.Range(0, inventory.Capacity)
                .Select(i => inventory.GetItemsAt(i).ToArray()).ToArray();
            for (int destination = 0; destination < slots.Length - 1; destination++)
            {
                int best = destination;
                for (int source = destination + 1; source < slots.Length; source++)
                {
                    if (Compare(slots[source], slots[best]) < 0 && Fits(cabinet, slots[source], destination) &&
                        Fits(cabinet, slots[destination], source))
                    {
                        best = source;
                    }
                }

                if (best == destination) { continue; }
                foreach (var item in slots[destination]) { inventory.ForceRemoveFromSlot(item, destination); }
                foreach (var item in slots[best]) { inventory.ForceRemoveFromSlot(item, best); }
                foreach (var item in slots[destination])
                {
                    inventory.ForceToSlot(item, best);
                    movedItems.Add(item);
                }
                foreach (var item in slots[best])
                {
                    inventory.ForceToSlot(item, destination);
                    movedItems.Add(item);
                }
                (slots[destination], slots[best]) = (slots[best], slots[destination]);
            }

            if (movedItems.Count > 0)
            {
                foreach (var item in movedItems) { cabinet.OnItemContained(item, triggerOnInsertedEffects: false); }
                inventory.CreateNetworkEvent();
            }
        }

        private static bool Fits(ItemContainer cabinet, Item[] stack, int slot) => stack.Length == 0 ||
            (stack.Length <= Math.Min(cabinet.GetMaxStackSize(slot), stack[0].Prefab.GetMaxStackSize(cabinet.Inventory)) &&
                stack.All(item => cabinet.CanBeContained(item, slot)));

        private static int Compare(Item[] left, Item[] right)
        {
            if (left.Length == 0 || right.Length == 0)
            {
                return (left.Length == 0).CompareTo(right.Length == 0);
            }
            int names = Comparer<LocalizedString>.Default.Compare(left[0].Name, right[0].Name);
            if (names != 0) { return names; }
            int sizes = right.Length.CompareTo(left.Length);
            return sizes != 0 ? sizes : right[0].ContainedItems.Count().CompareTo(left[0].ContainedItems.Count());
        }
    }
}
