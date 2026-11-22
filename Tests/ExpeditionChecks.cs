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
        var deconstruction = plugin.GetType("EnhancedExperience.CrewDeconstructionObjective", throwOnError: true);
        Check(side + " crew deconstruction extends the native marked-item objective",
            deconstruction.BaseType.FullName == "Barotrauma.AIObjectiveDeconstructItems");
        VerifyResourceClassification(deconstruction, args[3]);

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
            var updatePatches = patchInfo.Invoke(null, new object[] { escortTargets[0] });
            var prefixes = (System.Collections.IEnumerable)updatePatches.GetType().GetField("Prefixes").GetValue(updatePatches);
            Check(side + " attached crew initialization retry patch", prefixes.Cast<object>().Any(prefix =>
                ((MethodInfo)prefix.GetType().GetProperty("PatchMethod").GetValue(prefix)).Name == "EnsureCrewObjectives"));
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
        var dutyPriority = priorities.GetMethod("DutyPriority", BindingFlags.Static | BindingFlags.NonPublic);
        float Duty(bool assigned, bool available, bool emergency) =>
            (float)dutyPriority.Invoke(null, new object[] { assigned, available, true, emergency });
        var highestOrder = (float)manager.GetField("HighestOrderPriority").GetRawConstantValue();
        Check(side + " emergency overrides ordinary orders, yields to safety and clears when assignment ends",
            Duty(true, true, true) > highestOrder && Duty(true, true, true) < emergencyPriority &&
            Duty(true, false, true) == 0f && Duty(false, true, true) == 0f &&
            Duty(true, true, false) < orderFloor);

        var flooding = priorities.GetMethod("IsCriticalFlooding", BindingFlags.Static | BindingFlags.NonPublic);
        bool Critical(bool exterior, bool occupied, float opening, float water, bool responding) =>
            (bool)flooding.Invoke(null, new object[] { exterior, occupied, opening, water, responding });
        Check(side + " only severe occupied exterior flooding starts an emergency and response has hysteresis",
            Critical(true, true, 0.5f, 50f, false) && !Critical(true, false, 1f, 100f, false) &&
            !Critical(false, true, 1f, 100f, false) && !Critical(true, true, 0.4f, 45f, false) &&
            Critical(true, true, 0.4f, 45f, true) && !Critical(true, true, 0f, 100f, true));

        var bidType = priorities.GetNestedType("Bid`2", BindingFlags.NonPublic)
            .MakeGenericType(typeof(string), typeof(string));
        var assign = priorities.GetMethod("Assign", BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(typeof(string), typeof(string));
        System.Collections.IDictionary Match(params (string Worker, string Target, int Urgency, float Score)[] bids)
        {
            var candidates = Array.CreateInstance(bidType, bids.Length);
            for (int i = 0; i < bids.Length; i++)
            {
                var bid = bids[i];
                candidates.SetValue(Activator.CreateInstance(bidType, bid.Worker, bid.Target, bid.Urgency, bid.Score), i);
            }
            return (System.Collections.IDictionary)assign.Invoke(null, new object[] { candidates });
        }
        string Target(System.Collections.IDictionary assignments, string worker) =>
            assignments[worker] is { } bid ? (string)bidType.GetProperty("Target").GetValue(bid) : null;

        var assigned = Match(("mechanic", "pump", 0, 80f), ("assistant", "pump", 0, 30f),
            ("mechanic", "junction", 0, 50f), ("engineer", "junction", 0, 70f));
        Check(side + " simultaneous repairs have one worker and one target apiece",
            assigned.Count == 2 && Target(assigned, "mechanic") == "pump" && Target(assigned, "engineer") == "junction");
        assigned = Match(("assistant", "pump", 0, 30f), ("engineer", "junction", 0, 70f));
        Check(side + " an eligible helper takes over when the specialist is absent",
            assigned.Count == 2 && Target(assigned, "assistant") == "pump");
        assigned = Match(("mechanic", "pump", 0, 100f), ("mechanic", "breach", 2, 20f),
            ("assistant", "pump", 0, 30f));
        Check(side + " critical breach preempts committed repair and frees it for a helper",
            Target(assigned, "mechanic") == "breach" && Target(assigned, "assistant") == "pump");

        var inspection = priorities.GetMethod("GetInspectionPriority", BindingFlags.Static | BindingFlags.NonPublic);
        float Inspect(bool hasRoom, bool available) =>
            (float)inspection.Invoke(null, new object[] { hasRoom, available });
        assigned = Match(("engineer", "machine room", -1, 100f), ("mechanic", "machine room", -1, 60f),
            ("engineer", "junction", 0, 10f), ("mechanic", "electrical room", -1, 50f));
        Check(side + " actual repair preempts inspection and another bot takes the available room",
            Target(assigned, "engineer") == "junction" && Target(assigned, "mechanic") == "machine room");
        Check(side + " inspection yields to portable maintenance and orders",
            Inspect(true, true) > 0f && Inspect(true, true) < 30f && Inspect(true, false) == 0f &&
            Inspect(false, true) == 0f);

        Check(side + " crew repair uses native leak and equipment objectives",
            priorities.GetNestedType("AutoFixLeaks", BindingFlags.NonPublic).BaseType.FullName ==
                "Barotrauma.AIObjectiveFixLeaks" &&
            priorities.GetNestedType("AutoRepairSystems", BindingFlags.NonPublic).BaseType.FullName ==
                "Barotrauma.AIObjectiveRepairItems");
    }

    private static void VerifyResourceClassification(Type objective, string installedGame)
    {
        var classify = objective.GetMethod("IsResource", BindingFlags.Static | BindingFlags.NonPublic);
        var identifier = classify.GetParameters()[0].ParameterType;
        bool IsResource(string id, params string[] tags)
        {
            var values = Array.CreateInstance(identifier, tags.Length);
            for (int i = 0; i < tags.Length; i++)
            {
                values.SetValue(Activator.CreateInstance(identifier, tags[i]), i);
            }
            return (bool)classify.Invoke(null, new[] { Activator.CreateInstance(identifier, id), values });
        }

        Check("Mod resources are classified by loaded tags",
            new[] { "ore", "plant", "plantitem", "seed" }.All(tag => IsResource("modresource", tag)));
        Check("Manual-only items remain outside automatic selection",
            !IsResource("antidama1", "medical") && !IsResource("adrenalinegland", "smallitem") &&
            !IsResource("smallplanter", "planter"));

        var items = Directory.EnumerateFiles(Path.Combine(installedGame, "Content", "Items"), "*.xml",
                SearchOption.AllDirectories)
            .SelectMany(file => XDocument.Load(file).Descendants())
            .Where(item => item.Attribute("identifier") != null && item.Attribute("category") != null &&
                item.Elements("Deconstruct").Elements("Item").Any(product => product.Attribute("requiredotheritem") == null))
            .Where(item => IsResource((string)item.Attribute("identifier"), Tokens(item, "tags").ToArray()))
            .ToArray();
        Check("All 30 vanilla ore and shard recipes are classified",
            items.Count(item => Tokens(item, "tags").Contains("ore") ||
                (string)item.Attribute("identifier") is "oxygeniteshard" or "sulphuriteshard") == 30);
        Check("All 16 vanilla wild plants, seeds and crops with recipes are classified",
            items.Count(item => Tokens(item, "tags").Overlaps(new[] { "plant", "plantitem", "seed" })) == 16);
    }

    private static HashSet<string> Tokens(XElement element, string attribute) =>
        (element.Attributes().FirstOrDefault(entry => entry.Name.LocalName.Equals(attribute,
            StringComparison.OrdinalIgnoreCase))?.Value ?? "").Split(',', StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
