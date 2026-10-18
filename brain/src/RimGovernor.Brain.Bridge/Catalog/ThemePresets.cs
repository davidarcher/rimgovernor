using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The preset themes (#3493): data, so they live where the game's own *DefOf statics are readable. A def name comes
// from a DefOf field (nameof, which needs no loaded game) when the game has one. The game's DefOf classes carry only a
// fraction of its defs, so every name written as a string has no typed route; the string-named defs are checked
// against the installed catalog on resolution, and a missing one is reported as unavailable.

public sealed partial class DefinitionCatalog : P.IPresetCatalog
{
    /// <summary>Whether the load carries the named definition.</summary>
    public bool Has(P.PresetDefClass kind, string name) =>
        kind switch
        {
            P.PresetDefClass.Thing => _thingDefs.ContainsKey(name),
            P.PresetDefClass.Research => Research.ContainsKey(name),
            P.PresetDefClass.Precept => DefRow<RimWorld.PreceptDef>(name) is not null,
            P.PresetDefClass.Meme => DefRow<MemeDef>(name) is not null,
            P.PresetDefClass.RoomRole => DefRow<RoomRoleDef>(name) is not null,
            _ => throw new CatalogException($"unknown preset def class {kind}"),
        };
}

/// <summary>The themes the player can adopt, referenced by id from the save.</summary>
public static class ThemePresetCatalog
{
    /// <summary>Every preset in listing order.</summary>
    public static IReadOnlyList<P.ThemePreset> All { get; } =
    [
        NakedCannibals(),
        TranshumanistLab(),
        PacifistFarm(),
        TribalTraditionalists(),
        MechLord(),
        RoyalCourt(),
        MerchantRepublic(),
        Fortress(),
        Scholars(),
        Ranchers(),
        DrugCartel(),
        CultOfTheVoid(),
        ShipBuilders(),
    ];

    private static P.GoalRecord Goal(P.GoalKind kind, string target = "", int count = 0, string qualifier = "") =>
        new() { Kind = kind, Target = target, Count = count, Qualifier = qualifier };

    private static P.PresetMember Research(string project) => new("research." + project, Goal(P.GoalKind.ResearchProject, project));

    private static P.PresetMember Stock(string thing, int count) => new("stock." + thing, Goal(P.GoalKind.StockCount, thing, count));

    private static P.PresetMember Built(string thing, int count = 1) => new("built." + thing, Goal(P.GoalKind.BuiltCount, thing, count));

    private static P.PresetMember Herd(string race, int count) => new("animals." + race, Goal(P.GoalKind.Animals, race, count));

    private static P.PresetMember Room(string role) => new("room." + role, Goal(P.GoalKind.RoomRole, role));

    private static P.PresetMember Precept(string precept) => new("precept." + precept, Goal(P.GoalKind.Precept, precept));

    private static P.PresetMember Meme(string meme) => new("meme." + meme, Goal(P.GoalKind.Meme, meme));

    private static P.PresetMember Prefer(string key, P.PreferenceFragment fragment) => new("preference." + key, Preference: fragment);

    // Industrial is a TechLevel name; the research tier kind carries it as text.
    private static P.PresetMember Tier(TechLevel level) => new("tier." + level, Goal(P.GoalKind.ResearchTier, level.ToString()));

    private static P.ThemePreset NakedCannibals() =>
        new(
            "naked-cannibals",
            "Naked cannibals who worship monkeys",
            [
                Meme("Nudism"),
                Meme("Cannibal"),
                // No DefOf field exists for the nudity and insect-meat precepts; the cannibalism precept has one.
                Precept("Nudity_Male_Mandatory"),
                Precept("Nudity_Female_Mandatory"),
                Precept(nameof(PreceptDefOf.Cannibalism_Preferred)),
                Precept("InsectMeatEating_Loved"),
                // ThingDefOf has no Monkey field (no typed route); the race is a non-insect Animals-category ThingDef, which
                // PreceptWorker_Animal offers, and the installed catalog check reports it unavailable if absent.
                new("venerated.Monkey", Goal(P.GoalKind.VeneratedAnimal, "Monkey")),
                new("believers", Goal(P.GoalKind.Believers)),
                Prefer("apparel", new() { Apparel = P.ApparelPreference.Nude }),
                Prefer("diet", new() { Diet = [P.DietOverride.Cannibal, P.DietOverride.InsectMeat] }),
            ]);

    private static P.ThemePreset TranshumanistLab() =>
        new(
            "transhumanist-lab",
            "Transhumanist lab",
            [
                Meme("Transhumanist"),
                Precept("BodyMod_Approved"),
                Tier(TechLevel.Spacer),
                Research("Prosthetics"),
                Research("Bionics"),
                Research(nameof(ResearchProjectDefOf.Archogenetics)),
                Built("HiTechResearchBench"),
                Room(nameof(RoomRoleDefOf.Laboratory)),
            ]);

    private static P.ThemePreset PacifistFarm() =>
        new(
            "pacifist-farm",
            "Pacifist farm",
            [
                Prefer("risk", new() { Risk = P.RiskProfile.Cautious }),
                Prefer("slavery", new() { Forbidden = [P.ForbiddenMethod.Slavery] }),
                Precept("Slavery_Abhorrent"),
                Research("Hydroponics"),
                Built(nameof(ThingDefOf.HydroponicsBasin), 4),
                Stock(nameof(ThingDefOf.Hay), 600),
                Herd(nameof(ThingDefOf.Cow), 8),
                Herd(nameof(ThingDefOf.Chicken), 12),
                Room(nameof(RoomRoleDefOf.Hospital)),
            ]);

    private static P.ThemePreset TribalTraditionalists() =>
        new(
            "tribal-traditionalists",
            "Tribal traditionalists",
            [
                Meme("AnimalPersonhood"),
                Meme("NaturePrimacy"),
                Precept("AnimalConnection_Strong"),
                new("worn.tribalwear", Goal(P.GoalKind.WornCount, "Apparel_TribalA", 6)),
                Stock(nameof(ThingDefOf.WoodLog), 400),
                Herd("Horse", 4),
            ]);

    private static P.ThemePreset MechLord() =>
        new(
            "mech-lord",
            "Mech lord",
            [
                Research(nameof(ResearchProjectDefOf.BasicMechtech)),
                Research("StandardMechtech"),
                Research("HighMechtech"),
                Built("MechGestator"),
                Built("BasicRecharger", 2),
                Stock(nameof(ThingDefOf.ComponentIndustrial), 100),
                Stock(nameof(ThingDefOf.Steel), 800),
            ]);

    private static P.ThemePreset RoyalCourt() =>
        new(
            "royal-court",
            "Royal court",
            [
                Room(nameof(RoomRoleDefOf.ThroneRoom)),
                Built(nameof(ThingDefOf.Throne)),
                Research("NobleApparel"),
                Research("RoyalApparel"),
                Stock(nameof(ThingDefOf.Gold), 300),
                Stock(nameof(ThingDefOf.Silver), 900),
                Room(nameof(RoomRoleDefOf.Bedroom)),
            ]);

    private static P.ThemePreset MerchantRepublic() =>
        new(
            "merchant-republic",
            "Merchant republic",
            [
                Built(nameof(ThingDefOf.OrbitalTradeBeacon)),
                Built(nameof(ThingDefOf.CommsConsole)),
                Stock(nameof(ThingDefOf.Silver), 950),
                // The caravan kind has no def to name.
                new("caravan", Goal(P.GoalKind.Caravan, count: 1)),
                new("colonists", Goal(P.GoalKind.Colonists, count: 12)),
            ]);

    private static P.ThemePreset Fortress() =>
        new(
            "fortress",
            "Fortress",
            [
                Prefer("risk", new() { Risk = P.RiskProfile.Cautious }),
                Research("GunTurrets"),
                Research(nameof(ResearchProjectDefOf.MicroelectronicsBasics)),
                Research("Mortars"),
                Built(nameof(ThingDefOf.Turret_MiniTurret), 6),
                Built(nameof(ThingDefOf.Turret_Mortar), 2),
                Built(nameof(ThingDefOf.Sandbags), 20),
                new("territory.roofed", Goal(P.GoalKind.Territory, "roofed")),
                new("worn.plate-armor", Goal(P.GoalKind.WornCount, "Apparel_PlateArmor", 6)),
            ]);

    private static P.ThemePreset Scholars() =>
        new(
            "scholars",
            "Scholars",
            [
                Tier(TechLevel.Industrial),
                Research("MultiAnalyzer"),
                Built("HiTechResearchBench", 2),
                Built("Bookcase", 4),
                Room(nameof(RoomRoleDefOf.Laboratory)),
                Room("RecRoom"),
                Built("SculptureSmall", 2),
            ]);

    private static P.ThemePreset Ranchers() =>
        new(
            "ranchers",
            "Ranchers",
            [
                Meme("Rancher"),
                Room("Barn"),
                Herd(nameof(ThingDefOf.Cow), 10),
                Herd(nameof(ThingDefOf.Muffalo), 8),
                Herd(nameof(ThingDefOf.Chicken), 10),
                Stock(nameof(ThingDefOf.Leather_Plain), 300),
                Stock("Milk", 200),
                Stock(nameof(ThingDefOf.Hay), 800),
            ]);

    private static P.ThemePreset DrugCartel() =>
        new(
            "drug-cartel",
            "Drug cartel",
            [
                Research("DrugProduction"),
                Research("PsychiteRefining"),
                Research("GoJuiceProduction"),
                Built("DrugLab", 2),
                Stock(nameof(ThingDefOf.Flake), 300),
                Stock(nameof(ThingDefOf.Yayo), 300),
                Stock(nameof(ThingDefOf.Silver), 700),
            ]);

    private static P.ThemePreset CultOfTheVoid() =>
        new(
            "cult-of-the-void",
            "Cult of the void",
            [
                Research(nameof(ResearchProjectDefOf.BioferriteExtraction)),
                Research("VoidSculptures"),
                Research("EntityContainment"),
                Built(nameof(ThingDefOf.VoidMonolith)),
                Built(nameof(ThingDefOf.BioferriteHarvester)),
                Stock(nameof(ThingDefOf.Bioferrite), 200),
                Room("ContainmentCell"),
                Meme("Ritualist"),
            ]);

    private static P.ThemePreset ShipBuilders() =>
        new(
            "ship-builders",
            "Ship builders",
            [
                Research("ShipBasics"),
                Research("ShipCryptosleep"),
                Research("ShipReactor"),
                Research("ShipEngine"),
                Research("ShipComputerCore"),
                Research("ShipSensorCluster"),
                Built(nameof(ThingDefOf.Ship_Beam), 6),
                Built(nameof(ThingDefOf.Ship_Reactor)),
                Built(nameof(ThingDefOf.Ship_Engine)),
                Built(nameof(ThingDefOf.Ship_ComputerCore)),
                Built(nameof(ThingDefOf.Ship_CryptosleepCasket)),
                Built(nameof(ThingDefOf.Ship_SensorCluster)),
                // The ending kind has no def to name.
                new("ending.ship", Goal(P.GoalKind.Ending, "ship")),
            ]);
}
