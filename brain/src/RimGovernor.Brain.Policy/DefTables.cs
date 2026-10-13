namespace RimGovernor.Brain.Policy;

/// <summary>
/// One judgment table of game def names that policy keeps because the game offers no signal to derive it from.
/// Class is the CLR full name of the Def class every name must be a row of (a subclass row counts); the
/// dangling-reference gate in bridge checks each name against the recorded catalog, so a typo or a renamed def
/// fails a test rather than silently never matching.
/// </summary>
public sealed record DefTable(string Table, string Class, EquatableArray<string> Names)
{
    internal static DefTable Of(string table, string cls, IEnumerable<string> names) => new(table, cls, EquatableArray.From(names));
}

/// <summary>The items of the launcher's Rhadamant Sea Ice preset.</summary>
public sealed record RhadamantThingSet(string Silver, string Meal, string Wood, string Steel, string Component, string Medicine, string Rifle, string ShipChunk);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The Core items the launcher's scenario composer suggests for a starting thing. The composer takes any
    /// defName; this list only saves typing for common supplies.
    /// </summary>
    public static readonly IReadOnlyList<string> ScenarioThingChoices =
    [
        "Silver", "Gold", "WoodLog", "Steel", "Plasteel", "Uranium", "ComponentIndustrial", "ComponentSpacer",
        "MealSurvivalPack", "Pemmican", "MedicineHerbal", "MedicineIndustrial", "Gun_BoltActionRifle",
        "ShipChunk",
    ];

    public static readonly RhadamantThingSet RhadamantThings = new(
        "Silver", "MealSurvivalPack", "WoodLog", "Steel", "ComponentIndustrial", "MedicineIndustrial", "Gun_BoltActionRifle", "ShipChunk");

    private static List<string> RhadamantThingDefs()
    {
        var t = RhadamantThings;
        return [t.Silver, t.Meal, t.Wood, t.Steel, t.Component, t.Medicine, t.Rifle, t.ShipChunk];
    }

    /// <summary>The stock defs the wood latch (WoodMin, WoodTarget) floors: the colony stage waives the latch while every one has no source.</summary>
    public static readonly IReadOnlyList<string> WoodLatchDefs = ["WoodLog"];

    /// <summary>The one shell def BuildingProperties.IsMortar tests a loadable turret gun's fixed storage filter against (ThingDefOf.Shell_HighExplosive).</summary>
    public static readonly IReadOnlyList<string> MortarProbeShells = ["Shell_HighExplosive"];

    /// <summary>
    /// The items a Beggars quest's request may be answered with. Native gives whatever the lord toil requests,
    /// so which items the colony hands over is policy's choice, not a game rule.
    /// </summary>
    public static readonly IReadOnlyList<string> QuestGiftDefs = ["Beer", "MedicineHerbal", "MedicineIndustrial", "Penoxycyline", "Silver"];

    /// <summary>The ThingDefOf names the game's room-role workers score a furniture role by: a def of the name has the role.</summary>
    public static readonly IReadOnlyDictionary<string, FurnitureRole> GameRoomRoleDefs = new Dictionary<string, FurnitureRole>(StringComparer.Ordinal)
    {
        ["ToyBox"] = FurnitureRole.Toy,
        ["BabyDecoration"] = FurnitureRole.Decoration,
        ["Blackboard"] = FurnitureRole.Board,
        ["SchoolDesk"] = FurnitureRole.Desk,
    };

    /// <summary>The one meat def ThingDefGenerator_Meat gives a pawn def whose flesh type is not organic (ThingDefOf.Steel).</summary>
    public static readonly IReadOnlyList<string> NonFleshMeatDefs = ["Steel"];

    /// <summary>
    /// Lists every judgment table of def names. A table that holds def names registers here; a table derived
    /// from rows does not.
    /// </summary>
    public static IReadOnlyList<DefTable> DefTables() =>
    [
        DefTable.Of("unrecoveredParts", "Verse.HediffDef", SortedKeys(UnrecoveredParts)),
        DefTable.Of("keptBodyParts", "Verse.BodyPartDef", SortedKeys(KeptBodyParts)),
        DefTable.Of("herdTrainables", "RimWorld.TrainableDef", SortedKeys(HerdTrainables)),
        DefTable.Of("threatKinds", "Verse.PawnKindDef", SortedKeys(ThreatKinds)),
        DefTable.Of("threatWeapons", "Verse.ThingDef", SortedKeys(ThreatWeapons)),
        DefTable.Of("ArmorResearchRungs", "Verse.ResearchProjectDef", ArmorResearchRungs),
        DefTable.Of("gearValuables", "Verse.ThingDef", ResourceKeys(GearValuables)),
        DefTable.Of("traitStatWeights", "RimWorld.StatDef", SortedKeys(TraitStatWeights)),
        DefTable.Of("sociableTraits", "RimWorld.TraitDef", SortedKeys(SociableTraits)),
        DefTable.Of("traitThoughts", "RimWorld.ThoughtDef", TraitThoughts),
        DefTable.Of("suitePressureThoughts", "RimWorld.ThoughtDef", SortedKeys(SuitePressureThoughts)),
        DefTable.Of("GameRoomRoleDefs", "Verse.ThingDef", SortedKeys(GameRoomRoleDefs)),
        DefTable.Of("roomRoleScores", "Verse.RoomRoleDef", RoomRoleScoreRoles()),
        DefTable.Of("RoomRoleNamedDefs", "Verse.ThingDef", SortedKeys(RoomRoleNamedDefs)),
        DefTable.Of("roleEffects", "Verse.RoomRoleDef", RoleEffectRoles()),
        DefTable.Of("NonFleshMeatDefs", "Verse.ThingDef", NonFleshMeatDefs),
        DefTable.Of("MortarProbeShells", "Verse.ThingDef", MortarProbeShells),
        DefTable.Of("QuestGiftDefs", "Verse.ThingDef", QuestGiftDefs),
        DefTable.Of("roomSizeTable", "Verse.ThingDef", RoomSizeTable.Holds),
        DefTable.Of("ShelterHeatDef", "Verse.ThingDef", [ShelterHeatDef]),
        DefTable.Of("ShelterCoolDef", "Verse.ThingDef", [ShelterCoolDef]),
        DefTable.Of("WoodLatchDefs", "Verse.ThingDef", WoodLatchDefs),
        DefTable.Of("ScenarioThingChoices", "Verse.ThingDef", ScenarioThingChoices),
        DefTable.Of("RhadamantThings", "Verse.ThingDef", RhadamantThingDefs()),
        DefTable.Of("climateTable", "Verse.ThingDef", [ClimateTable.VentDef, ClimateTable.DoorDef, ClimateTable.WallDef, ClimateTable.RaceDef]),
        DefTable.Of("climateStats", "RimWorld.StatDef", [ClimateTable.ComfyMinStat, ClimateTable.ComfyMaxStat]),
        // lightTable: DarkSpeed (0.8) is StatPart_Glow's factor on WorkSpeedGlobal and MoveSpeed at glow 0, a
        // curve in the def that the bridge does not read; the standing and wall lamps are the buildings the
        // plan places (their reach is solved from each def's glow radius and colour; GroundScale,
        // FalloffBlend and DiagonalCost are the game's glow grid constants, read with ilspycmd). The research
        // behind the lamp pricing (every effect of darkness, which are priced and which deferred) is the
        // first comment of issue #2945.
        DefTable.Of("lightTable", "Verse.ThingDef", [LightTable.LampDef, LightTable.WallLampDef]),
        // moodGame (#2954): the stat the break thresholds scale, the thoughts the planner prices, and the
        // breaks the game gives no duration for or gives as a hediff; the numbers beside them are
        // MentalBreaker's literals.
        DefTable.Of("moodGame", "RimWorld.StatDef", [MoodGame.ThresholdStat]),
        DefTable.Of("moodThoughts", "RimWorld.ThoughtDef", MoodThoughtDefs()),
        DefTable.Of("moodUnpricedBreaks", "Verse.MentalBreakDef", MoodGame.Unpriced),
        DefTable.Of("moodHediffBreaks", "Verse.MentalBreakDef", SortedKeys(MoodGame.HediffBreaks)),
        DefTable.Of("moodBreakHediffs", "Verse.HediffDef", MoodBreakHediffs()),
        // seekerGame (#2975): the comfort and beauty needs and the thoughts their levels read through. The
        // category thresholds, the 150-tick need interval and the beauty impact factor are Need_Comfort's,
        // Need_Beauty's and Pawn_NeedsTracker's literals, read with ilspycmd; the rise, fall and base level
        // are the NeedDef rows'.
        // foodPoisonGame (#2955): the sickness a poisoned meal gives, the capacity of it that stands for lost
        // work (Consciousness, the judgment) and the job a vomit starts (JobDriver_Vomit: 300 to 900 ticks).
        DefTable.Of("foodPoisonHediff", "Verse.HediffDef", [FoodPoisonGame.Hediff]),
        DefTable.Of("foodPoisonCapacity", "Verse.PawnCapacityDef", [FoodPoisonGame.WorkCapacity]),
        DefTable.Of("foodPoisonVomit", "Verse.JobDef", [FoodPoisonGame.VomitJob]),
        // filthGame (#2988): the stat a colonist's cleaning speed is; UnitsPerRun is TryMakeFilth's default
        // count (ilspycmd), and the unpriced floor and tracked dirt are explained on FilthGame.
        DefTable.Of("filthGame", "RimWorld.StatDef", [FilthGame.SpeedStat]),
        DefTable.Of("seekerNeeds", "RimWorld.NeedDef", SeekerNeedDefs()),
        // fieldCrops (#2961): the crop a field is planned for while none is sown.
        DefTable.Of("fieldCrops", "Verse.ThingDef", FieldCrops),
        DefTable.Of("seekerThoughts", "RimWorld.ThoughtDef", SeekerThoughts()),
        // The wind path constants (windPathWidth ... windBlockedLoss) are CompPowerPlantWind's and
        // WindTurbineUtility.CalculateWindCells's, read with ilspycmd; keyed by the turbine def so a rename
        // fails the gate. perimeterTable: DetourBudgetTicks is the one judgment of the perimeter cut's
        // sapper-derived thickness (perimeter_cut.go); the wall def is the building the cut is walled with.
        DefTable.Of("perimeterTable", "Verse.ThingDef", [PerimeterTable.WallDef]),
        DefTable.Of("windPath", "Verse.ThingDef", [WindTurbineDefinition]),
        // interiorTable (#2956): the shelf and lamp the region interior wants beside benches and beds;
        // KitchenStoves is the judgment explained on InteriorTable.
        DefTable.Of("interiorTable", "Verse.ThingDef", [InteriorTable.ShelfDef, InteriorTable.LampDef]),
        // The power site price (#2999) is the market value of a conduit run and a wall cell: the catalog's
        // cost lists, keyed by the defs so a rename fails.
        DefTable.Of("siteConduit", "Verse.ThingDef", [PowerConduitDefinition]),
        // joyGame (#2997): the Joy need and the giver whose class is outdoors; the fall, interval and gain
        // scale are Need_Joy's and JoyUtility's literals (ilspycmd).
        DefTable.Of("joyGame", "RimWorld.NeedDef", [JoyGame.Need]),
        DefTable.Of("joyOutdoorGivers", "RimWorld.JoyGiverDef", JoyGame.OutdoorGivers),
        // haulGame (#2998): the stats the mining and chopping work and yield are scaled by; the pick interval
        // and hit are JobDriver_Mine's literals (ilspycmd) and MinerOwners the judgment explained on HaulTable.
        DefTable.Of("haulGame", "RimWorld.StatDef", [HaulGame.MiningSpeed, HaulGame.MiningYield, HaulGame.PlantWorkSpeed, HaulGame.PlantHarvestYield]),
    ];

    internal static List<string> ResourceKeys(IEnumerable<Resource> set) =>
        set.Select(r => r.Value).Order(StringComparer.Ordinal).ToList();

    internal static List<string> SortedKeys<TValue>(IReadOnlyDictionary<string, TValue> set) =>
        set.Keys.Order(StringComparer.Ordinal).ToList();

    internal static List<string> SortedKeys(IEnumerable<string> set) =>
        set.Order(StringComparer.Ordinal).ToList();
}
