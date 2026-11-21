using System;

namespace EnhancedExperience
{
    internal static class ExpeditionPolicy
    {
        internal static bool AllowAbyss(float? plannedDepth, float? currentDepth, double? flooding) =>
            plannedDepth > 6500 || (currentDepth >= 6500 && flooding >= 0.80);

        internal static double? FloodingRatio(double water, double volume) =>
            volume > 0 && double.IsFinite(volume) && double.IsFinite(water) ? water / volume : null;

        internal static int Missing(int required, int owned) => Math.Max(0, required - owned);

        internal static (int Type, int Quality, float Condition, float Distance, ushort Id) Rank(
            int type, int quality, float condition, float distanceSquared, ushort id) =>
            (type, -quality, -condition, distanceSquared, id);
    }
}
