namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    private static Resource R(string def) => new(def);

    private static Dictionary<Resource, T> ByDef<T>(params (string Def, T Value)[] rows) => rows.ToDictionary(r => R(r.Def), r => r.Value);

    /// <summary>A small slice of Core's item numbers shaped like the catalog's ItemFacts, for tests and fixtures that stand in for a loaded catalog; planners never read it.</summary>
    public static ItemFacts CoreItemFacts()
    {
        IReadOnlyList<string> Cats(params string[] c) => c;
        var acceptedStuff = new Dictionary<Resource, IReadOnlyList<string>>
        {
            [R("Sandbags")] = Cats("Fabric", "Leathery"),
        };
        foreach (var def in new[] { "Bed", "DoubleBed", "RoyalBed", "SculptureSmall", "SculptureLarge", "SculptureGrand" })
        {
            acceptedStuff[R(def)] = Cats("Metallic", "Woody", "Stony");
        }
        return new ItemFacts
        {
            Market = ByDef(
                ("Silver", 1.0), ("Steel", 1.9), ("Plasteel", 9.0), ("Uranium", 6.0), ("Gold", 10.0), ("Jade", 5.0), ("WoodLog", 1.2),
                ("BlocksSandstone", 0.9), ("BlocksGranite", 0.9), ("BlocksLimestone", 0.9), ("BlocksSlate", 0.9), ("BlocksMarble", 0.9),
                ("ComponentIndustrial", 32.0), ("MedicineHerbal", 18.0), ("MedicineIndustrial", 18.0), ("MedicineUltratech", 120.0), ("RawRice", 1.1)),
            Nutrition = ByDef(("RawRice", 0.05)),
            MedicalPotency = ByDef(("MedicineHerbal", 0.6), ("MedicineIndustrial", 1.0), ("MedicineUltratech", 1.6)),
            StuffBeauty = ByDef(
                ("WoodLog", 1.0), ("Steel", 1.0), ("Plasteel", 1.0), ("Uranium", 0.5), ("Silver", 2.0), ("Gold", 4.0), ("Jade", 2.5),
                ("BlocksSandstone", 1.1), ("BlocksGranite", 1.0), ("BlocksLimestone", 1.0), ("BlocksSlate", 1.1), ("BlocksMarble", 1.35)),
            StuffCategories = ByDef(
                ("WoodLog", Cats("Woody")), ("Steel", Cats("Metallic")), ("Plasteel", Cats("Metallic")), ("Uranium", Cats("Metallic")),
                ("Silver", Cats("Metallic")), ("Gold", Cats("Metallic")), ("Jade", Cats("Metallic")),
                ("BlocksSandstone", Cats("Stony")), ("BlocksGranite", Cats("Stony")), ("BlocksLimestone", Cats("Stony")),
                ("BlocksSlate", Cats("Stony")), ("BlocksMarble", Cats("Stony")),
                ("Cloth", Cats("Fabric")), ("Synthread", Cats("Fabric")), ("Hyperweave", Cats("Fabric")),
                ("Leather_Plain", Cats("Leathery")), ("Leather_Heavy", Cats("Leathery"))),
            AcceptedStuff = acceptedStuff,
            Categories = ByDef(
                ("RawRice", Cats("PlantFoodRaw")),
                ("BlocksSandstone", Cats("StoneBlocks")), ("BlocksGranite", Cats("StoneBlocks")), ("BlocksLimestone", Cats("StoneBlocks")),
                ("BlocksSlate", Cats("StoneBlocks")), ("BlocksMarble", Cats("StoneBlocks"))),
            Currency = R("Silver"),
            Wort = R("Wort"),

            // Core's deep deposits (ThingDef.deepCommonality > 0).
            DeepResources = [R("Gold"), R("Jade"), R("Plasteel"), R("Steel"), R("Uranium")],

            // Sculptures are Core's (Buildings_Art.xml), smallest first.
            Sculptures =
            [
                new Sculpture("Make_SculptureSmall", "SculptureSmall", new Cell(1, 1), 50, 18000),
                new Sculpture("Make_SculptureLarge", "SculptureLarge", new Cell(1, 1), 100, 30000),
                new Sculpture("Make_SculptureGrand", "SculptureGrand", new Cell(2, 2), 400, 105000),
            ],

            // Drugs are Core's, in the catalog's preference order.
            Drugs =
            [
                new Drug(R("Beer"), "Alcohol", Social: true), new Drug(R("SmokeleafJoint"), "Smokeleaf", Social: true), new Drug(R("PsychiteTea"), "Psychite", Social: true),
                new Drug(R("Flake"), "Psychite"), new Drug(R("GoJuice"), "GoJuice", Combat: true), new Drug(R("Luciferium"), "Luciferium"),
                new Drug(R("WakeUp"), "WakeUp"), new Drug(R("Yayo"), "Psychite", Combat: true),
            ],
            Chemicals = new Dictionary<string, Chemical>
            {
                ["Alcohol"] = new(true),
                ["Smokeleaf"] = new(true),
                ["Psychite"] = new(true),
                ["GoJuice"] = new(true),
                ["WakeUp"] = new(true),
                ["Luciferium"] = new(false),
            },
            Prevention = new Prevention(R("Penoxycyline"), 5, new EquatableArray<string>(["Malaria", "Plague"])),
        };
    }
}
