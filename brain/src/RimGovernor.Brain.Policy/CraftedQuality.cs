using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The quality a crafted item comes out at (#3850): <c>QualityUtility.GenerateQualityCreatedByPawn</c> as a distribution over
/// QualityCategory (Awful=0 through Legendary=6), confirmed with ilspycmd on Assembly-CSharp. The game draws
/// <c>Rand.GaussianAsymmetric(centre, 0.6, 0.8)</c> around the skill level's centre, truncates it to an int and clamps it to
/// Awful..Masterwork; a Masterwork is redrawn half the time with an upper width of 0.95. Inspired creativity adds two levels and
/// the crafter's ideoligion role its <c>RoleEffect_ProductionQualityOffset</c>, each capped at Legendary (<c>AddLevels</c>).
/// </summary>
public static class CraftedQualityRules
{
    /// <summary>The quality categories, Awful through Legendary.</summary>
    public const int Categories = 7;

    private const int Masterwork = 5, Legendary = 6, InspiredLevels = 2;
    private const double LowerWidth = 0.6, UpperWidth = 0.8, RedrawUpperWidth = 0.95, RedrawChance = 0.5;

    // GenerateQualityCreatedByPawn(int, bool)'s centre for each skill level 0..20 (its switch on relevantSkillLevel).
    private static readonly double[] Centre = [0.7, 1.1, 1.5, 1.8, 2, 2.2, 2.4, 2.6, 2.8, 2.95, 3.1, 3.25, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9, 4, 4.1, 4.2];

    // The uninspired, role-less odds per skill level, worked out once.
    private static readonly double[][] Base = [.. Enumerable.Range(0, Centre.Length).Select(level => Drawn(Centre[level]))];

    /// <summary>The odds of each quality an item made by <paramref name="crafter"/> comes out at.</summary>
    public static double[] Odds(GearCrafter crafter)
    {
        if (crafter.Skill < 0 || crafter.Skill >= Centre.Length)
        {
            throw new DomainException($"crafter skill {crafter.Skill} is outside the game's levels");
        }
        var odds = new double[Categories];
        var drawn = Base[crafter.Skill];
        for (var q = 0; q < Categories; q++)
        {
            var level = crafter.Inspired ? Math.Min(q + InspiredLevels, Legendary) : q;
            level = Math.Min(level + crafter.QualityOffset, Legendary);
            if (drawn[q] == 0) continue;
            if (level < 0) throw new DomainException($"crafter quality offset {crafter.QualityOffset} leaves no quality");
            odds[level] += drawn[q];
        }
        return odds;
    }

    /// <summary>
    /// The odds for the best of <paramref name="crafters"/> (#3850), the one whose items come out at the highest expected
    /// quality; the first on a tie. Throws <see cref="RequiredFactException"/> when there is none: the native read offers no bill
    /// nobody can craft.
    /// </summary>
    public static double[] BestOdds(IEnumerable<GearCrafter> crafters)
    {
        double[]? best = null;
        var bestMean = double.NegativeInfinity;
        foreach (var crafter in crafters)
        {
            var odds = Odds(crafter);
            var mean = 0.0;
            for (var q = 0; q < Categories; q++) mean += q * odds[q];
            if (mean > bestMean)
            {
                best = odds;
                bestMean = mean;
            }
        }
        return best ?? throw new RequiredFactException("gear bill crafters");
    }

    // The truncated, clamped draw and its Masterwork redraw, before inspiration and role.
    private static double[] Drawn(double centre)
    {
        var first = Truncated(centre, UpperWidth);
        var redraw = Truncated(centre, RedrawUpperWidth);
        var odds = new double[Categories];
        for (var q = 0; q < Masterwork; q++) odds[q] = first[q] + first[Masterwork] * RedrawChance * redraw[q];
        odds[Masterwork] = first[Masterwork] * (1 - RedrawChance) + first[Masterwork] * RedrawChance * redraw[Masterwork];
        return odds;
    }

    // (int)GaussianAsymmetric(centre, 0.6, upper) clamped to 0..5: a draw under 1 truncates to Awful (or below, clamped up),
    // one in [k, k+1) to k, one at 5 or over to Masterwork.
    private static double[] Truncated(double centre, double upper)
    {
        double Below(double x) => NormalCdf((x - centre) / (x <= centre ? LowerWidth : upper));
        var odds = new double[Masterwork + 1];
        odds[0] = Below(1);
        for (var q = 1; q < Masterwork; q++) odds[q] = Below(q + 1) - Below(q);
        odds[Masterwork] = 1 - Below(Masterwork);
        return odds;
    }

    // The standard normal CDF by erf (Abramowitz and Stegun 7.1.26, error under 1.5e-7).
    private static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + (0.3275911 * x));
        var erf = 1 - ((((((1.061405429 * t) - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
    }
}
