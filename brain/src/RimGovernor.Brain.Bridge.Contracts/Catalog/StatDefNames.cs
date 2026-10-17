namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// The StatDef names the planning views read, the CLR
/// classes they match by base class (never by name) and the room stat names. They are data the stat evaluator and the fact projections share.
/// </summary>
public static class StatDefNames
{
    // StatDefs.
    public const string StatMaxHitPoints = "MaxHitPoints";
    public const string StatBedRestEffectiveness = "BedRestEffectiveness";
    public const string StatWorkToBuild = "WorkToBuild";
    public const string StatDoorOpenSpeed = "DoorOpenSpeed";
    public const string StatFlammability = "Flammability";
    public const string StatWorkTableWorkSpeedFactor = "WorkTableWorkSpeedFactor";
    public const string StatMedicalTendQualityOffset = "MedicalTendQualityOffset";
    public const string StatResearchSpeedFactor = "ResearchSpeedFactor";
    public const string StatMeatAmount = "MeatAmount";

    /// <summary>The room stat the layout reads for impressiveness.</summary>
    public const string RoomStatImpressiveness = "Impressiveness";

    // CLR classes matched by base class.
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
