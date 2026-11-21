using System;
using System.IO;
using System.Collections.Generic;
using System.Xml.Linq;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using EnhancedExperience;

internal static class ExpeditionChecks
{
    private static void Check(string name, bool result)
    {
        Console.WriteLine($"{name}: {(result ? "PASS" : "FAIL")}");
        if (!result) { Environment.ExitCode = 1; }
    }

    private static void Main(string[] args)
    {
        Check("Unknown planned depth at 6300 m", !ExpeditionPolicy.AllowAbyss(null, 6300, 1));
        Check("Planned 6500 m", !ExpeditionPolicy.AllowAbyss(6500, 3000, 1));
        Check("Planned 6501 m", ExpeditionPolicy.AllowAbyss(6501, null, null));
        Check("6499 m, 80% flooded", !ExpeditionPolicy.AllowAbyss(null, 6499, 0.8));
        Check("6500 m, 79% flooded", !ExpeditionPolicy.AllowAbyss(null, 6500, 0.79));
        Check("6500 m, 80% flooded", ExpeditionPolicy.AllowAbyss(null, 6500, 0.8));
        Check("6500 m, 79.999% flooded", !ExpeditionPolicy.AllowAbyss(null, 6500, 0.79999));
        Check("Unknown flooding at 6800 m", !ExpeditionPolicy.AllowAbyss(null, 6800, null));
        Check("Volume-weighted flooding", ExpeditionPolicy.FloodingRatio(100, 1000) == 0.1);
        Check("Invalid total volume", ExpeditionPolicy.FloodingRatio(0, 0) == null);
        Check("Two, one and zero replacements",
            ExpeditionPolicy.Missing(2, 0) == 2 && ExpeditionPolicy.Missing(2, 1) == 1 &&
            ExpeditionPolicy.Missing(2, 2) == 0 && ExpeditionPolicy.Missing(2, 3) == 0);
        var ranks = new[]
        {
            ExpeditionPolicy.Rank(1, 5, 100, 0, 1),
            ExpeditionPolicy.Rank(0, 0, 100, 0, 2),
            ExpeditionPolicy.Rank(0, 1, 50, 0, 3),
            ExpeditionPolicy.Rank(0, 1, 60, 10, 4),
            ExpeditionPolicy.Rank(0, 1, 60, 5, 6),
            ExpeditionPolicy.Rank(0, 1, 60, 5, 5)
        };
        Check("Type, quality, condition, distance and tie-break",
            ranks.OrderBy(rank => rank).Select(rank => rank.Id).SequenceEqual(new ushort[] { 5, 6, 4, 3, 2, 1 }));

        string game = args[0], side = args[1], pluginPath = args[2];
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(game, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var gameAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, side + ".dll"));
        var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
        var objective = plugin.GetType("EnhancedExperience.AIObjectivePrepareForExpedition", throwOnError: true);
        Check(side + " objective loads", objective.BaseType.FullName == "Barotrauma.AIObjective");
        var medical = plugin.GetType("EnhancedExperience.MedicalCleanupObjective", throwOnError: true);
        Check(side + " medical cleanup objective loads", medical.BaseType.FullName == "Barotrauma.AIObjectiveCleanupItems");
        Check(side + " cabinet organizer loads", plugin.GetType("EnhancedExperience.MedicalCabinetOrganizer", throwOnError: true)
            .GetMethod("Organize", BindingFlags.Static | BindingFlags.NonPublic) != null);
        var deconstruction = plugin.GetType("EnhancedExperience.MedicalDeconstructionObjective", throwOnError: true);
        Check(side + " medical deconstruction objective loads",
            deconstruction.BaseType.FullName == "Barotrauma.AIObjectiveDeconstructItems");
        VerifyDeconstruction(deconstruction, args[3]);

        var entry = plugin.GetType("EnhancedExperience.ExpeditionPlugin", throwOnError: true);
        var instance = Activator.CreateInstance(entry);
        var factory = gameAssembly.GetType("Barotrauma.AIObjectiveManager", throwOnError: true).GetMethod("CreateObjective");
        var patchInfo = Assembly.Load("0Harmony").GetType("HarmonyLib.Harmony", throwOnError: true).GetMethod("GetPatchInfo");
        bool IsPatched()
        {
            var info = patchInfo.Invoke(null, new object[] { factory });
            if (info == null) { return false; }
            var owners = (System.Collections.Generic.IEnumerable<string>)info.GetType().GetProperty("Owners").GetValue(info);
            return owners.Contains("EnhancedExperience.PrepareForExpedition");
        }
        try
        {
            entry.GetMethod("Initialize").Invoke(instance, null);
            Check(side + " factory patch", IsPatched());
        }
        finally
        {
            entry.GetMethod("Dispose").Invoke(instance, null);
        }
        Check(side + " factory unpatch", !IsPatched());
    }

    private static void VerifyDeconstruction(Type objective, string installedGame)
    {
        var expected = new Dictionary<string, string>
        {
            ["aquaticpoppy"] = "opium,opium,opium",
            ["yeastshroom"] = "ethanol,ethanol,ethanol",
            ["slimebacteria"] = "antibiotics,antibiotics,antibiotics",
            ["adrenalinegland"] = "adrenaline,adrenaline",
            ["swimbladder"] = "antibloodloss1,antibloodloss1,antibloodloss1,stabilozine,stabilozine",
            ["smallmudraptoregg"] = "alienblood",
            ["mucusball"] = "antibleeding1,calcium,paralyxis",
            ["hallucinogenicbufotoxin"] = "antibloodloss1,paralyxis",
            ["oxygeniteshard"] = "liquidoxygenite,liquidoxygenite",
            ["smallcrawleregg"] = "sulphuricacid",
            ["sulphuriteshard"] = "sulphuricacid,sulphuricacid",
            ["divingknifeunique"] = "iron,morbusine"
        };
        var whitelist = (System.Collections.IEnumerable)objective
            .GetField("Allowed", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Check("Exactly twelve automatic ingredients",
            expected.Keys.ToHashSet().SetEquals(whitelist.Cast<object>().Select(id => id.ToString())));

        var recipes = Directory.EnumerateFiles(Path.Combine(installedGame, "Content", "Items"), "*.xml", SearchOption.AllDirectories)
            .SelectMany(file => XDocument.Load(file).Descendants())
            .Where(element => element.Attribute("identifier") is { } id && expected.ContainsKey(id.Value) &&
                element.Elements().Any(child => child.Name.LocalName == "Deconstruct"))
            .ToLookup(element => element.Attribute("identifier").Value);
        foreach (var entry in expected)
        {
            var data = recipes[entry.Key].ToArray();
            var output = data.SelectMany(element => element.Elements("Deconstruct"))
                .SelectMany(recipe => recipe.Elements("Item"))
                .SelectMany(item => Enumerable.Repeat(item.Attribute("identifier").Value,
                    (int?)item.Attribute("amount") ?? 1)).OrderBy(id => id, StringComparer.Ordinal);
            Check(entry.Key + " installed recipe", data.Length == 1 && string.Join(",", output) == entry.Value);
        }
    }
}
