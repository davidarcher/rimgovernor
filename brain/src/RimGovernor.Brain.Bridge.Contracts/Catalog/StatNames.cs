namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's package-level constants of <c>internal/bridge</c>: the StatDef names the planning views read, the CLR
/// classes they match by base class (never by name) and the room stat names. Spread over the Go catalog files;
/// gathered here because they are data the stat evaluator and the fact projections share.
/// </summary>
public static partial class BridgeConstants
{
    // StatDefs (catalog_planning.go, catalog_terrain.go, catalog_items.go, catalog_races.go, race_rules.go, ...).
    public const string StatMaxHitPoints = "MaxHitPoints";
    public const string StatBedRestEffectiveness = "BedRestEffectiveness";
    public const string StatWorkToBuild = "WorkToBuild";
    public const string StatDoorOpenSpeed = "DoorOpenSpeed";
    public const string StatCleanliness = "Cleanliness";
    public const string StatBeauty = "Beauty";
    public const string StatFlammability = "Flammability";
    public const string StatComfort = "Comfort";
    public const string StatWorkTableWorkSpeedFactor = "WorkTableWorkSpeedFactor";
    public const string StatMedicalTendQualityOffset = "MedicalTendQualityOffset";
    public const string StatResearchSpeedFactor = "ResearchSpeedFactor";
    public const string StatNutrition = "Nutrition";
    public const string StatDeteriorationRate = "DeteriorationRate";
    public const string StatMarketValue = "MarketValue";
    public const string StatCarryingCapacity = "CarryingCapacity";
    public const string StatWildness = "Wildness";
    public const string StatMinimumHandlingSkill = "MinimumHandlingSkill";
    public const string StatLeatherAmount = "LeatherAmount";
    public const string StatMeatAmount = "MeatAmount";

    /// <summary>The room stat the layout reads for impressiveness (catalog_room_stats.go).</summary>
    public const string RoomStatImpressiveness = "Impressiveness";

    // CLR classes matched by base class (catalog_planning.go and the files that name the rest).
    public const string ClassPowerComp = "RimWorld.CompProperties_Power";
    public const string ClassGlowerComp = "RimWorld.CompProperties_Glower";
    public const string ClassExplosiveComp = "RimWorld.CompProperties_Explosive";
    public const string ClassToxifierComp = "RimWorld.CompProperties_Toxifier";
    public const string ClassPolluteComp = "RimWorld.CompProperties_PolluteOverTime";
    public const string ClassWasteComp = "RimWorld.CompProperties_WasteProducer";
    public const string ClassDeathrestComp = "RimWorld.CompProperties_DeathrestBindable";
    public const string ClassMechChargerThng = "RimWorld.Building_MechCharger";
    public const string ClassGenepackContainerComp = "RimWorld.CompProperties_GenepackContainer";
    public const string ClassBedThing = "RimWorld.Building_Bed";
    public const string ClassBed = "RimWorld.Building_Bed";
    public const string ClassChangeableProjectileComp = "RimWorld.CompProperties_ChangeableProjectile";
    public const string ClassTempControlComp = "RimWorld.CompTempControl";
    public const string ClassPowerPlantComp = "RimWorld.CompPowerPlant";
    public const string ClassWorkTableThing = "RimWorld.Building_WorkTable";
    public const string ClassSarcophagus = "RimWorld.Building_Sarcophagus";
    public const string ClassDoor = "RimWorld.Building_Door";
    public const string ClassSolarPlantComp = "RimWorld.CompPowerPlantSolar";
    public const string ClassWindPlantComp = "RimWorld.CompPowerPlantWind";
    public const string ClassConduitComp = "RimWorld.CompPowerTransmitter";
    public const string ClassPlainBuilding = "Verse.Building";
}
