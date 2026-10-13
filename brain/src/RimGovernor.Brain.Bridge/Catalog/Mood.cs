using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_mood.go: the mood price's catalog input (#2954): the break curve built from the MentalBreakDefs and
// MentalStateDefs, and the stages of the thoughts the planner prices (the dark, the role thoughts). A catalog without the
// threshold stat (a hand-built one) has an unknown model, not an error.
//
// A break counts in its tier when it is generic: not anomalous and not gated on a trait, gene or precept. Its weight is
// baseCommonality and its length the mean days its state lasts (P.PolicyFunctions.StateDays), or for a hediff break the
// mean of the hediff's disappear range. A break the game gives no length for (an unpriced one, or a modded one with
// neither a state nor a hediff) is left out, which makes the cost a lower bound.

public sealed partial class DefinitionCatalog
{
    private P.MoodModel MoodModel()
    {
        var game = P.PolicyFunctions.MoodGame();
        if (DefRow<StatDef>(game.ThresholdStat) is not { } stat)
        {
            return new P.MoodModel();
        }

        var tiers = new P.MoodTierModel[3];
        foreach (var row in Rows<MentalBreakDef>().Values.OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            if (row.anomalousBreak || row.requiredTrait is not null || row.requiredGene is not null || row.requiredPrecept is not null || game.Unpriced.Contains(row.defName))
            {
                continue;
            }

            var tier = row.intensity switch
            {
                MentalBreakIntensity.Minor => P.MoodTier.Minor,
                MentalBreakIntensity.Major => P.MoodTier.Major,
                MentalBreakIntensity.Extreme => P.MoodTier.Extreme,
                _ => (P.MoodTier?)null,
            };
            if (tier is not { } found || BreakDays(row, game) is not { } days)
            {
                continue;
            }

            tiers[(int)found] = tiers[(int)found].Add(row.baseCommonality, days);
        }

        var stages = new Dictionary<string, IReadOnlyList<P.MoodStage>>(StringComparer.Ordinal);
        var seeker = P.PolicyFunctions.SeekerGame();
        foreach (var name in new[] { game.DarkThought, seeker.Comfort.Thought, seeker.Beauty.Thought }.Concat(game.RoleThoughts.Values))
        {
            if (ThoughtFacts(name) is { } facts)
            {
                stages[name] = facts.Stages;
            }
        }

        return new P.MoodModel { Break = new P.MoodBreakModel { Threshold = Decimal32(stat.defaultBaseValue), Tiers = tiers }, Stages = stages };
    }

    // The mean days a break takes its colonist out of work.
    private double? BreakDays(MentalBreakDef row, P.MoodGameTable game)
    {
        if (row.mentalState is { } state)
        {
            return P.PolicyFunctions.StateDays(state.minTicksBeforeRecovery, state.maxTicksBeforeRecovery, state.recoveryMtbDays);
        }

        if (!game.HediffBreaks.TryGetValue(row.defName, out var hediffName) || DefRow<HediffDef>(hediffName) is not { } hediff)
        {
            return null;
        }

        return hediff.comps.Seq().OfType<HediffCompProperties_Disappears>().FirstOrDefault() is { } gone
            ? (gone.disappearsAfterTicks.min + gone.disappearsAfterTicks.max) / 2.0 / GameTime.TicksPerDay
            : null;
    }
}
