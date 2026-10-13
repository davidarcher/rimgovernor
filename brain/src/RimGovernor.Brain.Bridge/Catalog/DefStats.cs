using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_def_stats.go: per-def stat facts the planners read.

/// <summary>One candidate stone stuff and its per-wall cost (Go <c>WallMaterial</c>).</summary>
public sealed record WallMaterial(string Stuff, IReadOnlyList<Amount> Costs);

/// <summary>A resource and a unit count (Go bridge <c>Amount</c>).</summary>
public readonly record struct Amount(string Resource, long Units);

public sealed partial class DefinitionCatalog
{
    /// <summary>The def's <c>ThingDef.stackLimit</c>: the most one stack of it holds, which is what one storage cell holds of it.</summary>
    public long StackLimit(string def) => ThingRow(def).stackLimit;

    /// <summary><c>ThingDef.useHitPoints</c>: the def has hit points that can be damaged and repaired.</summary>
    public bool UsesHitPoints(string def) => ThingRow(def).useHitPoints;

    /// <summary>
    /// The order the colony repairs a structure in: 0 for what holds the temperature or the power, or a medical bed, 1
    /// for what holds a roof, a work table or a bed, 2 for the rest.
    /// </summary>
    public int RepairPriority(string def, bool medicalBed)
    {
        var row = ThingRow(def);
        var bed = typeof(Building_Bed).IsAssignableFrom(row.thingClass);
        if (HasComp<CompProperties_TempControl>(row) || (row.GetCompProperties<CompProperties_Power>()?.compClass is { } powerClass && typeof(CompPowerPlant).IsAssignableFrom(powerClass)) || (bed && medicalBed))
        {
            return 0;
        }

        return row.holdsRoof || typeof(Building_WorkTable).IsAssignableFrom(row.thingClass) || bed ? 1 : 2;
    }

    /// <summary>Whether the def is a crib (<c>building.bed_crib</c>): native refuses a crib for prisoners (<c>Building_Bed.RoomCanBePrisonCell</c>).</summary>
    public bool BedCrib(string def) => ThingRow(def).building is { bed_crib: true };

    /// <summary><c>BuildingProperties.bed_humanlike</c> of the def.</summary>
    public bool BedHumanlike(string def) => ThingRow(def).building is { bed_humanlike: true };

    /// <summary>
    /// The stuffs of the given stuff category that can make def (<c>GenStuff.AllowedStuffsFor</c>), by defName, each with
    /// the def's adjusted cost when made from it (<c>CostListAdjusted(stuff)</c>).
    /// </summary>
    public IReadOnlyList<WallMaterial> StuffMaterials(string def, string category)
    {
        var result = new List<WallMaterial>();
        foreach (var stuff in AllowedStuffs(def))
        {
            if (!ThingRow(stuff).stuffProps.categories.Seq().Any(c => c.defName == category))
            {
                continue;
            }

            result.Add(new WallMaterial(stuff, [.. AdjustedCosts(def, stuff).Select(cost => new Amount(cost.Def, cost.Units))]));
        }

        return result;
    }

    /// <summary>
    /// The game's <c>BuildingProperties.IsMortar</c> of the def: a turret whose gun has a primary verb with an overhead
    /// projectile, or whose gun takes loaded shells and accepts the high-explosive shell. A def that is not a turret is
    /// not a mortar.
    /// </summary>
    public bool IsMortar(string def)
    {
        if (ThingRow(def).building?.turretGunDef is not { } gunDef)
        {
            return false;
        }

        var gun = ThingRow(gunDef.defName);
        foreach (var verb in gun.Verbs.Seq().Where(v => v.isPrimary && v.defaultProjectile is not null))
        {
            if (ThingRow(verb.defaultProjectile.defName).projectile is { flyOverhead: true })
            {
                return true;
            }
        }

        return HasComp<CompProperties_ChangeableProjectile>(gun)
            && FilterAccepts(gun.building?.fixedStorageSettings?.filter, MortarProbeShells[0]);
    }
}
