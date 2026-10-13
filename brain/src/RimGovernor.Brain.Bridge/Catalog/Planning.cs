using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_planning.go: the planning views' shared reads. A comp is GetCompProperties<T>() (the first comp of the
// class or a subclass), a class test is typeof(X).IsAssignableFrom, so the Go class-name consts and the
// CompFloat/CompString reflection helpers have no counterpart.

/// <summary>The StatDef names the planning views read besides the floor stats.</summary>
public static class PlanningStatNames
{
    public const string MaxHitPoints = "MaxHitPoints";
    public const string BedRestEffectiveness = "BedRestEffectiveness";
    public const string WorkToBuild = "WorkToBuild";
    public const string DoorOpenSpeed = "DoorOpenSpeed";
}

public sealed partial class DefinitionCatalog
{
    /// <summary><c>ThingDef.BuildableByPlayer</c>: the def has a designation category.</summary>
    public static bool Buildable(ThingDef row) => row.BuildableByPlayer;

    /// <summary><c>BuildableDef.BuildableByPlayer</c> of a TerrainDef.</summary>
    public static bool TerrainBuildable(TerrainDef row) => row.BuildableByPlayer;

    /// <summary>The furniture roles the game's room-role code scores the def for by name (Toy, Decoration, Board, Desk): at most one.</summary>
    public static IReadOnlyList<string> GameRoomRoles(string name) =>
        GameRoomRoleDefs.TryGetValue(name, out var role) ? [role.Value] : [];

    /// <summary>Whether the def carries a comp of class <typeparamref name="T"/> or a subclass (the game's <c>GetCompProperties&lt;T&gt;()</c> is non-null).</summary>
    public static bool HasComp<T>(ThingDef row)
        where T : CompProperties =>
        row.GetCompProperties<T>() is not null;

    /// <summary>The names of the definitions that carry a room-role furniture role: a role the game names, a baby bed (flagged bed_crib) or a deathrest-bindable building. Sorted.</summary>
    public IReadOnlyList<string> RoomRoleRows() =>
        [.. _thingDefs.Values
            .Where(row => Buildable(row) && (GameRoomRoles(row.defName).Count > 0 || row.building is { bed_crib: true } || HasComp<CompProperties_DeathrestBindable>(row)))
            .Select(row => row.defName)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// The base consumption (negative when it generates) of the def's power comp (CompProperties_Power or a subclass
    /// such as a battery), null when the def has none. It is the def's static draw: the research upgrade factors a
    /// finished project applies are the game's to state, not the row's.
    /// </summary>
    public static double? PowerDraw(ThingDef row) =>
        row.GetCompProperties<CompProperties_Power>() is { } power ? GameMembers.BasePowerConsumption(power) : null;

    /// <summary>The market value of def's adjusted cost list at stuff (empty for a def not made from stuff).</summary>
    public double CostValue(string def, string stuff) => CostValueOf(def, stuff);

    private double CostValueOf(string def, string stuff) =>
        AdjustedCosts(def, stuff).Sum(cost => (double)StatValue(cost.Def, string.Empty, StatNames.MarketValue) * cost.Units);

    /// <summary>The names of the Building_MechCharger defs, sorted.</summary>
    public IReadOnlyList<string> MechChargers() =>
        [.. _thingDefs.Values
            .Where(row => Buildable(row) && typeof(Building_MechCharger).IsAssignableFrom(row.thingClass))
            .Select(row => row.defName)
            .Order(StringComparer.Ordinal)];

    /// <summary>The names of the buildable defs carrying the genepack container comp (the gene bank), sorted.</summary>
    public IReadOnlyList<string> GeneBanks() =>
        [.. _thingDefs.Values
            .Where(row => Buildable(row) && HasComp<CompProperties_GenepackContainer>(row))
            .Select(row => row.defName)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// The least market value of def's adjusted cost list over its allowed stuffs, or at no stuff for a def not made
    /// from stuff: the price of the cheapest way to build it, whatever is in stock.
    /// </summary>
    public double CheapestCostValue(string def)
    {
        var stuffs = AllowedStuffs(def);
        return stuffs.Count == 0 ? CostValueOf(def, string.Empty) : stuffs.Min(stuff => CostValueOf(def, stuff));
    }
}
