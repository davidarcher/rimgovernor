using RimWorld;

namespace RimGovernor.Brain.Runtime;

/// <summary>The authored starting supplies shared by the launcher and new-colony route.</summary>
public sealed record RhadamantThingSet(string Silver, string Meal, string Wood, string Steel, string Component, string Medicine, string Rifle, string ShipChunk);

public static class ScenarioSupplies
{
    // Core exposes no DefOf field or exact typed route for the preset's bolt-action rifle.
    private const string BoltActionRifle = "Gun_BoltActionRifle";

    /// <summary>Typing shortcuts; the scenario composer accepts any installed definition name.</summary>
    public static readonly IReadOnlyList<string> Suggestions =
    [
        nameof(ThingDefOf.Silver), nameof(ThingDefOf.Gold), nameof(ThingDefOf.WoodLog), nameof(ThingDefOf.Steel),
        nameof(ThingDefOf.Plasteel), nameof(ThingDefOf.Uranium), nameof(ThingDefOf.ComponentIndustrial), nameof(ThingDefOf.ComponentSpacer),
        nameof(ThingDefOf.MealSurvivalPack), nameof(ThingDefOf.Pemmican), nameof(ThingDefOf.MedicineHerbal), nameof(ThingDefOf.MedicineIndustrial),
        BoltActionRifle, nameof(ThingDefOf.ShipChunk),
    ];

    public static readonly RhadamantThingSet Rhadamant = new(
        nameof(ThingDefOf.Silver), nameof(ThingDefOf.MealSurvivalPack), nameof(ThingDefOf.WoodLog), nameof(ThingDefOf.Steel),
        nameof(ThingDefOf.ComponentIndustrial), nameof(ThingDefOf.MedicineIndustrial), BoltActionRifle, nameof(ThingDefOf.ShipChunk));
}
