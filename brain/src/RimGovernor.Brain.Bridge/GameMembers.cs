using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace RimGovernor.Brain.Bridge;

// The private members of the game's own types that the catalog reads. They are typed accessors the runtime
// binds at JIT time (UnsafeAccessor), so a member the game renames fails at the first call rather than as a
// reflection string lookup that returns nothing. Every other game member the catalog reads is public.

internal static class GameMembers
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "basePowerConsumption")]
    internal static extern ref float BasePowerConsumption(CompProperties_Power comp);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "fuelMultiplier")]
    internal static extern ref float FuelMultiplier(CompProperties_Refuelable comp);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "WindSpeedRange")]
    internal static extern ref FloatRange WindSpeedRange(WindManager? instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "thingDef")]
    internal static extern ref ThingDef? SingleDefThing(StockGenerator_SingleDef generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "thingDefs")]
    internal static extern ref List<ThingDef>? MultiDefThings(StockGenerator_MultiDef generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "excludedThingDefs")]
    internal static extern ref List<ThingDef>? StockTagExcludedThings(StockGenerator_Tag generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "categoryDef")]
    internal static extern ref ThingCategoryDef? StockCategory(StockGenerator_Category generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "excludedThingDefs")]
    internal static extern ref List<ThingDef>? StockExcludedThings(StockGenerator_Category generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "excludedCategories")]
    internal static extern ref List<ThingCategoryDef>? StockExcludedCategories(StockGenerator_Category generator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "damageAmountBase")]
    internal static extern ref int DamageAmountBase(ProjectileProperties projectile);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "armorPenetrationBase")]
    internal static extern ref float ArmorPenetrationBase(ProjectileProperties projectile);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "externalViolenceForMechanoids")]
    internal static extern ref bool ExternalViolenceForMechanoids(DamageDef damage);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "forcedMissRadius")]
    internal static extern ref float ForcedMissRadius(VerbProperties verb);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "diseases")]
    internal static extern ref List<BiomeDiseaseRecord>? BiomeDiseases(BiomeDef biome);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "roomStat")]
    internal static extern ref RoomStatDef RoomStat(StatPart_RoomStat part);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "buildingTagsSet")]
    internal static extern ref HashSet<string>? ForbiddenBuildingTagsSet(RoomRequirement_ForbiddenBuildings requirement);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "bloodDef")]
    internal static extern ref ThingDef? BloodDef(RaceProperties race);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "fleshType")]
    internal static extern ref FleshTypeDef? FleshType(RaceProperties race);
}
