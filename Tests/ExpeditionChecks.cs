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
        var expeditionReturn = plugin.GetType("EnhancedExperience.ExpeditionReturn", throwOnError: true);
        Check(side + " expedition return extends stock navigation",
            expeditionReturn.BaseType.FullName == "Barotrauma.AIObjectiveReturn");
        var medical = plugin.GetType("EnhancedExperience.MedicalCleanupObjective", throwOnError: true);
        Check(side + " medical cleanup objective loads", medical.BaseType.FullName == "Barotrauma.AIObjectiveCleanupItems");
        Check(side + " cabinet organizer loads", plugin.GetType("EnhancedExperience.MedicalCabinetOrganizer", throwOnError: true)
            .GetMethod("Organize", BindingFlags.Static | BindingFlags.NonPublic) != null);
        var deconstruction = plugin.GetType("EnhancedExperience.MedicalDeconstructionObjective", throwOnError: true);
        Check(side + " medical deconstruction objective loads",
            deconstruction.BaseType.BaseType.FullName == "Barotrauma.AIObjectiveDeconstructItems");
        VerifyDeconstruction(deconstruction, args[3]);
        var mineral = plugin.GetType("EnhancedExperience.MineralDeconstructionObjective", throwOnError: true);
        Check(side + " mechanic and doctor share the automatic deconstruction objective",
            mineral.BaseType == deconstruction.BaseType);
        VerifyVanillaMinerals(args[3]);

        var entry = plugin.GetType("EnhancedExperience.ExpeditionPlugin", throwOnError: true);
        var instance = Activator.CreateInstance(entry);
        Check(side + " stock order dismissal can be called",
            entry.GetField("DismissOrder", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) is Delegate);
        var objectiveManager = gameAssembly.GetType("Barotrauma.AIObjectiveManager", throwOnError: true);
        var factory = objectiveManager.GetMethod("CreateObjective");
        var setOrder = objectiveManager.GetMethod("SetOrder",
            new[] { gameAssembly.GetType("Barotrauma.Order", throwOnError: true), typeof(bool) });
        var autonomousFactory = objectiveManager.GetMethod("CreateAutonomousObjectives");
        VerifyCrewRepairPriorities(plugin, objectiveManager, side);
        var defense = plugin.GetType("EnhancedExperience.SecurityDefense", throwOnError: true);
        const BindingFlags internalStatic = BindingFlags.Static | BindingFlags.NonPublic;
        var slots = defense.GetMethod("IntruderSlots", internalStatic);
        int Slots(int officers, int intruders, bool external) =>
            (int)slots.Invoke(null, new object[] { officers, intruders, external });
        Check(side + " security reserves an external officer and limits duplicate assignments",
            Slots(1, 1, true) == 1 && Slots(2, 3, true) == 1 &&
            Slots(3, 1, true) == 1 && Slots(3, 3, true) == 2 &&
            Slots(3, 3, false) == 3 && Slots(3, 0, true) == 0);
        var railgun = defense.GetMethod("IsRailgun", internalStatic);
        var identifier = railgun.GetParameters()[0].ParameterType;
        object Id(string value) => Activator.CreateInstance(identifier, new object[] { value });
        bool IsRailgun(string prefab, string parent) =>
            (bool)railgun.Invoke(null, new[] { Id(prefab), Id(parent) });
        Check(side + " automatic turret selection excludes railguns and permits flak",
            IsRailgun("railgun", "") && IsRailgun("legacyrailgun", "") &&
            IsRailgun("custom_variant", "railgun") && !IsRailgun("flakcannon", "") &&
            !IsRailgun("coilgun", ""));

        var turret = gameAssembly.GetType("Barotrauma.Items.Components.Turret", throwOnError: true);
        var turretOperation = turret.GetMethod("CrewAIOperate");
        var turretLaunch = turret.GetMethod("TryLaunch", BindingFlags.Instance | BindingFlags.NonPublic);
        var structure = gameAssembly.GetType("Barotrauma.Structure", throwOnError: true);
        var character = gameAssembly.GetType("Barotrauma.Character", throwOnError: true);
        var hullDamage = structure.GetMethod("AddDamage",
            new[] { typeof(int), typeof(float), character, typeof(bool), typeof(bool) });
        var defenseTargets = new[] { turretOperation, turretLaunch, hullDamage };
        var patrol = plugin.GetType("EnhancedExperience.MaintenancePatrol", throwOnError: true);
        var needsService = patrol.GetMethod("NeedsService", BindingFlags.Static | BindingFlags.NonPublic);
        Check(side + " maintenance charge threshold includes 0%, 50% and 90%",
            new[] { 0f, 50f, 90f }.All(charge => (bool)needsService.Invoke(null, new object[] { charge })));
        Check(side + " maintenance charge threshold excludes charge above 90% and invalid values",
            new[] { 90.001f, 91f, 100f, -1f, float.NaN }
                .All(charge => !(bool)needsService.Invoke(null, new object[] { charge })));
        var patchInfo = Assembly.Load("0Harmony").GetType("HarmonyLib.Harmony", throwOnError: true).GetMethod("GetPatchInfo");
        var escortMethods = new (string Type, string Method)[]
        {
            ("HumanAIController", "Update"),
            ("HumanAIController", "SpotEnemies"),
            ("AIObjective", "OnDeselected"),
            ("AIObjectiveGoTo", "Act"),
            ("AIObjectiveGoTo", "get_CloseEnough"),
            ("AIObjectiveCombat", "GetPriority"),
            ("AIObjectiveFindSafety", "GetPriority"),
            ("Items.Components.Holdable", "Pick"),
            ("Items.Components.RepairTool", "FixBody"),
            ("Items.Components.RepairTool", "Repair")
        };
        const BindingFlags declaredInstance = BindingFlags.Instance | BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.NonPublic;
        var escortTargets = escortMethods.Select(target =>
            gameAssembly.GetType("Barotrauma." + target.Type, throwOnError: true)
                .GetMethod(target.Method, declaredInstance) ??
                    throw new InvalidOperationException("Missing escort target " + target)).ToArray();
        var inventoryPut = gameAssembly.GetType("Barotrauma.Inventory", throwOnError: true)
            .GetMethod("PutItem", declaredInstance);
        bool IsPatched(MethodInfo method)
        {
            var info = patchInfo.Invoke(null, new object[] { method });
            if (info == null) { return false; }
            var owners = (System.Collections.Generic.IEnumerable<string>)info.GetType().GetProperty("Owners").GetValue(info);
            return owners.Contains("EnhancedExperience.PrepareForExpedition");
        }
        try
        {
            entry.GetMethod("Initialize").Invoke(instance, null);
            Check(side + " factory patch", IsPatched(factory));
            Check(side + " Return order transition patch", IsPatched(setOrder));
            Check(side + " crew autonomy patch", IsPatched(autonomousFactory));
            Check(side + " escort patches", escortTargets.All(IsPatched));
            Check(side + " expedition acquisition tracking patch", IsPatched(inventoryPut));
            Check(side + " security targeting and hull-damage patches", defenseTargets.All(IsPatched));
        }
        finally
        {
            entry.GetMethod("Dispose").Invoke(instance, null);
        }
        Check(side + " factory unpatch", !IsPatched(factory));
        Check(side + " Return order transition unpatch", !IsPatched(setOrder));
        Check(side + " crew autonomy unpatch", !IsPatched(autonomousFactory));
        Check(side + " escort unpatch", !escortTargets.Any(IsPatched));
        Check(side + " expedition acquisition tracking unpatch", !IsPatched(inventoryPut));
        Check(side + " security unpatch", !defenseTargets.Any(IsPatched));
    }

    private static void VerifyCrewRepairPriorities(Assembly plugin, Type manager, string side)
    {
        var priorities = plugin.GetType("EnhancedExperience.CrewRepairPriorities", throwOnError: true);
        var selectPriority = priorities.GetMethod("SelectPriority", BindingFlags.Static | BindingFlags.NonPublic);
        float Priority(bool targets, bool order, bool leaks) =>
            (float)selectPriority.Invoke(null, new object[] { targets, order, leaks });
        var orderFloor = (float)manager.GetField("LowestOrderPriority").GetRawConstantValue();
        var emergencyPriority = (float)manager.GetField("EmergencyObjectivePriority").GetRawConstantValue();
        var leaksPriority = Priority(true, false, true);
        var repairPriority = Priority(true, false, false);
        Check(side + " crew repair priority hierarchy",
            repairPriority > orderFloor - 1f && leaksPriority > repairPriority &&
            leaksPriority < orderFloor && leaksPriority < emergencyPriority);
        Check(side + " crew repair yields to active orders and falls back without targets",
            Priority(true, true, true) == 0f && Priority(true, true, false) == 0f &&
            Priority(false, false, true) == 0f && Priority(false, false, false) == 0f);
        Check(side + " crew repair uses native leak and equipment objectives",
            priorities.GetNestedType("AutoFixLeaks", BindingFlags.NonPublic).BaseType.FullName ==
                "Barotrauma.AIObjectiveFixLeaks" &&
            priorities.GetNestedType("AutoRepairSystems", BindingFlags.NonPublic).BaseType.FullName ==
                "Barotrauma.AIObjectiveRepairItems");
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

    private static void VerifyVanillaMinerals(string installedGame)
    {
        var items = Directory.EnumerateFiles(Path.Combine(installedGame, "Content", "Items"), "*.xml",
                SearchOption.AllDirectories)
            .SelectMany(file => XDocument.Load(file).Descendants())
            .Where(item => item.Attribute("identifier") != null &&
                (item.Attribute("category") != null || item.Attribute("variantof") != null))
            .ToArray();
        var minerals = items.Where(item => Tokens(item, "tags").Contains("ore") ||
            (string)item.Attribute("identifier") is "oxygeniteshard" or "sulphuriteshard").ToArray();
        var medicalInputs = items.SelectMany(item => item.Elements().Where(recipe =>
                recipe.Name.LocalName.ToLowerInvariant() is ("fabricate" or "fabricable" or "fabricableitem") &&
                (Tokens(item, "category").Contains("medical") || Tokens(item, "tags").Overlaps(new[] { "medical", "poison" }) ||
                 Tokens(recipe, "suitablefabricators").Contains("medicalfabricator") ||
                 recipe.Elements("RequiredSkill").Any(skill => (string)skill.Attribute("identifier") == "medical"))))
            .SelectMany(recipe => recipe.Elements("RequiredItem"))
            .Select(item => (string)item.Attribute("identifier"))
            .ToHashSet();
        medicalInputs.UnionWith(new[] { "oxygeniteshard", "sulphuriteshard" });

        var processable = minerals.Where(item => item.Elements("Deconstruct").Elements("Item")
                .Any(product => product.Attribute("requiredotheritem") == null))
            .Select(item => (string)item.Attribute("identifier")).ToHashSet();
        Check("All 28 vanilla ore recipes remain available for automatic processing",
            processable.Except(medicalInputs).ToHashSet().SetEquals(
                ("amblygonite aragonite bornite brockite cassiterite chalcopyrite chamosite chrysoprase cryolite diamond " +
                 "esperite galena graphite hydroxyapatite ilmenite ironore langbeinite lazulite polyhalite pyromorphite " +
                 "quartz sphalerite stannite sylvite thorianite titanite triphylite uraniumore").Split(' ')));
        var protectedMinerals = minerals.Select(item => (string)item.Attribute("identifier"))
            .Where(medicalInputs.Contains).ToHashSet();
        Check("Vanilla medical minerals and shards (" + string.Join(", ", protectedMinerals.Order()) + ")",
            protectedMinerals.SetEquals(new[] { "dementonite", "fulgurium", "oxygeniteshard", "paralyxis", "sulphuriteshard" }));
    }

    private static HashSet<string> Tokens(XElement element, string attribute) =>
        (element.Attributes().FirstOrDefault(entry => entry.Name.LocalName.Equals(attribute,
            StringComparison.OrdinalIgnoreCase))?.Value ?? "").Split(',', StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
