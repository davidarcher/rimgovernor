#nullable enable
using System;
using System.Linq;
using System.Reflection;

// The bundle's field-mask contract (#360, #648) against the real bridge:
// NativeBundleMasks' predicates, which the readers consult at source, and its
// reference projection, over a full row. Absent keeps everything, an empty
// mask is the slim family, and each bit keeps exactly its own block together
// with the issue rows about it. Live pawns (that an omitted block's native
// accessors are never called) are native evidence, not this probe's.
internal static class BundleMaskProof
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly string[] PawnBits = { "includeGearDetail", "includeInventory", "includeCapacities", "includeSurgeryBills", "includeBackstory", "includeTraits", "includeRelations" };

    private const string Gear = "{\"thing\":{\"id\":\"Apparel_1\"},\"stuff\":\"Cloth\",\"quality\":\"Normal\",\"hitPoints\":50,\"maxHitPoints\":100,\"conditionFraction\":0.5,\"apparelLayers\":[\"OnSkin\"],\"bodyPartGroups\":[\"Torso\"]}";
    private const string FullPawn = "{\"pawns\":[{\"pawn\":{\"id\":\"Human1\"},"
        + "\"equipment\":{\"equipped\":[" + Gear + "],\"apparel\":[" + Gear + "],\"inventoryWeapons\":[" + Gear + "],\"inventoryItemCount\":3,\"carriedThingId\":\"Thing_2\",\"armed\":true,"
        + "\"issues\":[{\"field\":\"equipped.forced\"},{\"field\":\"equipped.armor_sharp\"},{\"field\":\"inventory_weapons.locked\"},{\"field\":\"inventory_weapons.insulation_heat\"}]},"
        + "\"health\":{\"pain\":0.1,\"capacities\":[{\"defName\":\"Moving\",\"level\":1}],\"surgeryBills\":[{\"id\":\"Bill_1\",\"recipe\":\"Anesthetize\"}],"
        + "\"issues\":[{\"field\":\"stable_rest_eligible\"},{\"field\":\"bed_id\"}]},"
        + "\"biography\":{\"childhood\":{\"defName\":\"Child\"},\"adulthood\":{\"defName\":\"Adult\"},\"biologicalAgeYears\":30,\"chronologicalAgeYears\":31,"
        + "\"traits\":[{\"defName\":\"Kind\",\"degree\":0}],\"skills\":[{\"definition\":{\"defName\":\"Shooting\"},\"level\":4}],"
        + "\"issues\":[{\"field\":\"title\"},{\"field\":\"title_source\"},{\"field\":\"childhood.label\"},{\"field\":\"incapable_sources\"}]},"
        + "\"social\":{\"highExpectations\":false,\"relations\":[{\"other\":{\"id\":\"Human2\"},\"opinion\":5}]}}]}";

    internal static void Run(Assembly bridge, Action<bool, string> check)
    {
        var masks = bridge.GetType("HomeBridge.BridgeTools.NativeBundleMasks", true)!;
        Func<string, string, object> parse = (name, json) => {
            var parser = bridge.GetType("RimGovernor.Protocol.Observations." + name, true)!.GetProperty("Parser")!.GetValue(null)!;
            return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
        };
        Func<object, string> json = message => message.ToString()!;
        Func<string, object?, bool> predicate = (name, mask) => (bool)masks.GetMethod(name, Flags)!.Invoke(null, new[] { mask })!;
        Func<object, object?, object> apply = (snapshot, mask) => masks.GetMethods(Flags).Single(m => m.Name == "Apply" && m.GetParameters()[0].ParameterType == snapshot.GetType()).Invoke(null, new[] { snapshot, mask })!;

        var predicates = new[] { "GearDetail", "Inventory", "Capacities", "SurgeryBills", "Backstory", "Traits", "Relations" };
        foreach (var name in predicates) check(predicate(name, null), "absent pawn mask keeps " + name);
        var empty = parse("PawnFields", "{}");
        foreach (var name in predicates) check(!predicate(name, empty), "empty pawn mask drops " + name);
        for (var i = 0; i < PawnBits.Length; i++)
        {
            var mask = parse("PawnFields", "{\"" + PawnBits[i] + "\":true}");
            for (var j = 0; j < predicates.Length; j++) check(predicate(predicates[j], mask) == (i == j), PawnBits[i] + " keeps exactly its block (" + predicates[j] + ")");
            var explicitFalse = parse("PawnFields", "{\"" + PawnBits[i] + "\":false}");
            check(!predicate(predicates[i], explicitFalse), "explicit false drops " + predicates[i]);
        }

        // The absent mask is identity; the full row is not mutated by it.
        var full = parse("PawnSnapshot", FullPawn);
        check(json(apply(full, null)) == json(parse("PawnSnapshot", FullPawn)), "absent mask leaves the full row untouched");

        Func<string, string> projected = mask => json(apply(parse("PawnSnapshot", FullPawn), parse("PawnFields", mask)));
        var slim = projected("{}");
        foreach (var gone in new[] { "\"stuff\"", "\"quality\"", "\"hitPoints\"", "\"maxHitPoints\"", "apparelLayers", "inventoryWeapons", "inventoryItemCount", "carriedThingId",
            "capacities", "surgeryBills", "childhood", "adulthood", "biologicalAgeYears", "chronologicalAgeYears", "traits", "relations",
            "armor_sharp", "insulation_heat", "inventory_weapons.locked", "\"title\"", "title_source" })
            check(!slim.Contains(gone), "slim mask drops " + gone);
        foreach (var kept in new[] { "conditionFraction", "bodyPartGroups", "\"armed\"", "equipped.forced", "\"pain\"", "stable_rest_eligible", "bed_id",
            "skills", "incapable_sources", "highExpectations", "Apparel_1" })
            check(slim.Contains(kept), "slim mask keeps mandatory " + kept);

        var expectations = new (string bit, string[] present)[] {
            ("includeGearDetail", new[] { "\"stuff\"", "\"quality\"", "\"hitPoints\"", "apparelLayers", "armor_sharp" }),
            ("includeInventory", new[] { "inventoryItemCount", "carriedThingId", "inventory_weapons.locked" }),
            ("includeCapacities", new[] { "capacities" }),
            ("includeSurgeryBills", new[] { "surgeryBills" }),
            ("includeBackstory", new[] { "childhood", "adulthood", "biologicalAgeYears", "\"title\"", "title_source" }),
            ("includeTraits", new[] { "traits" }),
            ("includeRelations", new[] { "relations" }),
        };
        foreach (var (bit, present) in expectations)
        {
            var one = projected("{\"" + bit + "\":true}");
            foreach (var field in present) check(one.Contains(field) && !slim.Contains(field), bit + " alone restores " + field);
            foreach (var (other, others) in expectations.Where(e => e.bit != bit))
                foreach (var field in others.Where(f => !present.Contains(f))) check(!one.Contains(field), bit + " alone still drops " + other + "'s " + field);
        }
        var all = "{" + string.Join(",", PawnBits.Select(b => "\"" + b + "\":true")) + "}";
        check(projected(all) == json(parse("PawnSnapshot", FullPawn)), "every bit set equals the absent mask");
        check(projected("{\"includeInventory\":true}").Contains("inventoryWeapons") && !projected("{\"includeInventory\":true}").Contains("\"stuff\""), "inventory without gear detail keeps slim inventory weapons");

        // Population and research: absent, empty and each bit.
        const string population = "{\"persons\":[{\"pawn\":{\"pawn\":{\"id\":\"Human1\"}},\"admitted\":true,\"ownedBed\":{\"building\":{\"id\":\"Bed_1\"}},\"nutritionPerDay\":1.6}],\"supportedInteractions\":[{\"defName\":\"MaintainOnly\"}]}";
        Func<string?, string> pop = mask => json(apply(parse("PopulationSnapshot", population), mask == null ? null : parse("PopulationFields", mask)));
        check(pop(null) == json(parse("PopulationSnapshot", population)), "absent population mask keeps the census");
        foreach (var (bit, field) in new[] { ("includeOwnedBed", "ownedBed"), ("includeNutrition", "nutritionPerDay"), ("includeSupportedInteractions", "supportedInteractions") })
        {
            check(!pop("{}").Contains(field), "empty population mask drops " + field);
            check(pop("{\"" + bit + "\":true}").Contains(field), bit + " keeps " + field);
            check(pop("{}").Contains("admitted") && pop("{}").Contains("Human1"), "population identity survives the mask");
        }
        const string research = "{\"projects\":[{\"project\":{\"defName\":\"Smithing\"},\"baseCost\":700,\"apparentCost\":700,\"costFactor\":1,\"progress\":10,\"progressFraction\":0.01,"
            + "\"techprintsApplied\":0,\"techprintsNeeded\":1,\"finished\":false,\"canStart\":true,\"requiredFacilities\":[\"MultiAnalyzer\"],\"unlocks\":[{\"defName\":\"Longsword\"}],"
            + "\"issues\":[{\"field\":\"progress_fraction\"}]}]}";
        Func<string?, string> res = mask => json(apply(parse("ResearchSnapshot", research), mask == null ? null : parse("ResearchFields", mask)));
        check(res(null) == json(parse("ResearchSnapshot", research)), "absent research mask keeps the page");
        foreach (var (bit, fields) in new[] { ("includeUnlocks", new[] { "unlocks" }), ("includeCosts", new[] { "baseCost", "apparentCost", "costFactor", "\"progress\"", "progressFraction", "techprintsNeeded", "progress_fraction" }), ("includeFacilities", new[] { "requiredFacilities" }) })
            foreach (var field in fields)
            {
                check(!res("{}").Contains(field), "empty research mask drops " + field);
                check(res("{\"" + bit + "\":true}").Contains(field), bit + " keeps " + field);
            }
        check(res("{}").Contains("Smithing") && res("{}").Contains("canStart"), "research identity and lock facts survive the mask");
    }
}
