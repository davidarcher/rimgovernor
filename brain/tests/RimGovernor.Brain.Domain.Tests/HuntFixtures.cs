using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whole hunt reads for tests that set only the members they exercise.</summary>
internal static class HuntFixtures
{
    /// <summary>A coupled capture's hunt section with no hunter and no prey, in clear weather.</summary>
    internal static HuntActivityRead NoHunt => new() { Hunters = [], Prey = [], WeatherAccuracy = 1, Findings = [] };

    /// <summary>A pawn of the hunt read with no gear, body, melee, revenge, skills or health, no store for its corpse and no meat.</summary>
    internal static HuntActorRead Actor(string pawn, string kind, Cell location, double age) => new(pawn, kind, location, age)
    {
        Xenotype = "",
        Weapon = null,
        Gun = null,
        Body = null,
        Melee = null,
        Revenge = [],
        Apparel = [],
        Skills = [],
        Traits = [],
        Health = [],
        MissingParts = [],
        Available = false,
        Hauls = false,
        CorpseStore = new HuntCorpseStore.NoStore(),
        Downed = false,
        Mental = false,
        Herd = "",
        Meat = 0,
        MeatNutrition = 0,
    };

    /// <summary>A ranged shot of <paramref name="range"/> cells and <paramref name="accuracy"/>, the other numbers a bolt-action rifle's.</summary>
    internal static HuntGunRead Gun(double range, double accuracy = 0.97) =>
        new(range, accuracy, 1, 1, 1, 1, 0.65, 0.85, 0.9, 0.8, true, false, 102, 90, 1, 0, 18, 0.27, true, 0.0125);
}
