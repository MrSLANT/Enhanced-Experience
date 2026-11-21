using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;

namespace EnhancedExperience
{
    internal sealed class MineralDeconstructionObjective : AutomaticDeconstructionObjective
    {
        private static readonly Identifier Mechanic = "mechanic".ToIdentifier();
        private static readonly Identifier Ore = "ore".ToIdentifier();
        private static readonly Identifier Medical = "medical".ToIdentifier();
        private static readonly Identifier Poison = "poison".ToIdentifier();
        private static readonly Identifier MedicalFabricator = "medicalfabricator".ToIdentifier();
        private static object medicalPackageHash;
        private static HashSet<Identifier> medicalIngredients;

        public MineralDeconstructionObjective(Character character, AIObjectiveManager manager, float priorityModifier)
            : base(character, manager, priorityModifier) { }

        internal static bool IsMechanic(Character character) => character.Info?.Job?.Prefab.Identifier == Mechanic;

        protected override bool DismissWhenEmpty => false;
        protected override bool ResetWhenClearingIgnoreList => false;
        protected override bool ReleaseCollectedItem => true;

        internal static bool IsMineral(ItemPrefab prefab) => prefab.Tags.Contains(Ore) ||
            prefab.Identifier.Value is "oxygeniteshard" or "sulphuriteshard";

        private static bool IsMedical(ItemPrefab prefab) =>
            prefab.Category.HasFlag(MapEntityCategory.Medical) || prefab.Tags.Contains(Medical) || prefab.Tags.Contains(Poison);

        private static bool IsProtected(ItemPrefab prefab)
        {
            if (MedicalDeconstructionObjective.IsAutomaticIngredient(prefab.Identifier) || IsMedical(prefab)) { return true; }

            var hash = ContentPackageManager.EnabledPackages.MergedHash;
            if (medicalIngredients == null || !Equals(medicalPackageHash, hash))
            {
                medicalIngredients = ItemPrefab.Prefabs
                    .SelectMany(product => product.FabricationRecipes.Values.Where(recipe =>
                        IsMedical(product) || recipe.SuitableFabricatorIdentifiers.Contains(MedicalFabricator) ||
                        recipe.RequiredSkills.Any(skill => skill.Identifier == Medical)))
                    .SelectMany(recipe => recipe.RequiredItems)
                    .SelectMany(requirement => requirement.ItemPrefabs)
                    .Where(IsMineral)
                    .Select(ingredient => ingredient.Identifier)
                    .ToHashSet();
                medicalPackageHash = hash;
            }
            return medicalIngredients.Contains(prefab.Identifier);
        }

        protected override bool IsAutomaticTarget(ItemPrefab prefab) =>
            IsMineral(prefab) && !IsProtected(prefab) &&
            prefab.DeconstructItems.Any(recipe => recipe.RequiredOtherItem.Length == 0 && recipe.Amount > 0);

        protected override bool IsValidTarget(Item item) =>
            item != null && (!IsMineral(item.Prefab) || !IsProtected(item.Prefab)) && base.IsValidTarget(item);

        protected override bool IsReady(Deconstructor device) =>
            base.IsReady(device) && !device.OutputContainer.Inventory.Locked;

        protected override bool FitsRecipe(Deconstructor device, Item item)
        {
            if (!device.InputContainer.Inventory.CanBePut(item)) { return false; }

            float condition = item.Condition / item.MaxCondition;
            var products = item.Prefab.DeconstructItems.Where(recipe =>
                (recipe.RequiredOtherItem.Length == 0 || !device.DeconstructItemsSimultaneously) &&
                recipe.IsValidDeconstructor(device.Item) && recipe.Amount > 0 &&
                condition >= recipe.MinCondition && condition <= recipe.MaxCondition).ToArray();
            if (products.Length == 0) { return false; }

            var output = device.OutputContainer.Inventory;
            var reservedSlots = new HashSet<int>();
            foreach (var group in products.GroupBy(recipe => recipe.ItemIdentifier))
            {
                if (group.Key == item.Prefab.Identifier || !ItemPrefab.Prefabs.TryGet(group.Key, out var product))
                {
                    return false;
                }

                // Only guaranteed full-condition products can share an occupied stack.
                bool fullCondition = group.All(recipe => recipe.OutConditionMin == 1f && recipe.OutConditionMax == 1f &&
                    (!recipe.CopyCondition || condition == 1f));
                int remaining = group.Sum(recipe => recipe.Amount);
                for (int slot = 0; fullCondition && remaining > 0 && slot < output.Capacity; slot++)
                {
                    if (output.GetItemAt(slot) != null && output.CanBePutInSlot(product, slot, product.Health, 0))
                    {
                        remaining -= output.HowManyCanBePut(product, slot, product.Health);
                    }
                }
                for (int slot = 0; remaining > 0 && slot < output.Capacity; slot++)
                {
                    if (output.GetItemAt(slot) != null || reservedSlots.Contains(slot) ||
                        !output.CanBePutInSlot(product, slot, null, 0)) { continue; }
                    reservedSlots.Add(slot);
                    remaining -= fullCondition ? output.HowManyCanBePut(product, slot, product.Health) : 1;
                }
                if (remaining > 0) { return false; }
            }
            return true;
        }
    }
}
