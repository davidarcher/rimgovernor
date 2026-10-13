using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace RimGovernor.Brain.Bridge;

// The private members of the game's own types that the catalog reads. They are typed accessors the runtime
// binds at JIT time (UnsafeAccessor), so a member the game renames fails at the first call rather than as a
// reflection string lookup that returns nothing. Every other game member the catalog reads is public.

public static class GameMembers
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "basePowerConsumption")]
    public static extern ref float BasePowerConsumption(CompProperties_Power comp);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "fuelMultiplier")]
    public static extern ref float FuelMultiplier(CompProperties_Refuelable comp);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "WindSpeedRange")]
    public static extern ref FloatRange WindSpeedRange(WindManager? instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "thingDef")]
    public static extern ref ThingDef? SingleDefThing(StockGenerator_SingleDef generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "thingDefs")]
    public static extern ref List<ThingDef>? MultiDefThings(StockGenerator_MultiDef generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "excludedThingDefs")]
    public static extern ref List<ThingDef>? StockTagExcludedThings(StockGenerator_Tag generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "categoryDef")]
    public static extern ref ThingCategoryDef? StockCategory(StockGenerator_Category generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "excludedThingDefs")]
    public static extern ref List<ThingDef>? StockExcludedThings(StockGenerator_Category generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "excludedCategories")]
    public static extern ref List<ThingCategoryDef>? StockExcludedCategories(StockGenerator_Category generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "damageAmountBase")]
    public static extern ref int DamageAmountBase(ProjectileProperties projectile);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "armorPenetrationBase")]
    public static extern ref float ArmorPenetrationBase(ProjectileProperties projectile);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "AssaultTimeBeforeGiveUp")]
    public static extern ref IntRange AssaultTimeBeforeGiveUp(LordJob_AssaultColony? instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "externalViolenceForMechanoids")]
    public static extern ref bool ExternalViolenceForMechanoids(DamageDef damage);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "forcedMissRadius")]
    public static extern ref float ForcedMissRadius(VerbProperties verb);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "diseases")]
    public static extern ref List<BiomeDiseaseRecord>? BiomeDiseases(BiomeDef biome);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "roomStat")]
    public static extern ref RoomStatDef RoomStat(StatPart_RoomStat part);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "buildingTagsSet")]
    public static extern ref HashSet<string>? ForbiddenBuildingTagsSet(RoomRequirement_ForbiddenBuildings requirement);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "bloodDef")]
    public static extern ref ThingDef? BloodDef(RaceProperties race);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "fleshType")]
    public static extern ref FleshTypeDef? FleshType(RaceProperties race);
}
