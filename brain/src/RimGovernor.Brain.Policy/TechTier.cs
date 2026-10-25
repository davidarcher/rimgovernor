using TechLevel = RimGovernor.Contracts.Wire.Defs.TechLevel;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// How far the colony's construction technology has come: the one ordered fact every layout decision reads
/// instead of research or the faction tech level directly. It is derived from finished research with the player
/// faction's tech level as a floor, because RimWorld never advances a faction's techLevel with research: a tribe
/// that has finished Stonecutting and Electricity still reads Neolithic.
/// </summary>
public enum TechTier
{
    Camp,
    Masonry,
    Powered,
    Industrial,
    Spacer,
}

public static partial class TechTierRules
{
    /// <summary>The research projects that raise the tier to each tier above Camp: any one of them does.</summary>
    internal static readonly IReadOnlyDictionary<TechTier, string[]> Unlocks = new Dictionary<TechTier, string[]>
    {
        [TechTier.Masonry] = ["Stonecutting"],
        [TechTier.Powered] = ["Electricity"],
        [TechTier.Industrial] = ["Machining", "Fabrication"],
        [TechTier.Spacer] = ["AdvancedFabrication"],
    };

    /// <summary>
    /// Derives the tech tier from the finished research and the faction tech level. A faction floor satisfies
    /// every tier at or below it; research raises the tier above the floor one condition at a time:
    ///
    ///   Camp        default
    ///   Masonry     Stonecutting finished, or faction &gt;= Medieval
    ///   Powered     Masonry and Electricity finished
    ///   Industrial  Powered and (Machining or Fabrication finished, or faction &gt;= Industrial)
    ///   Spacer      Industrial and (AdvancedFabrication finished, or faction &gt;= Spacer)
    ///
    /// A faction floor satisfies every tier at or below it, so an Industrial faction stands at Industrial
    /// without Electricity in its finished list.
    ///
    /// Finished research is a total census. An Undefined faction level is a boundary failure; the tier
    /// is never inferred from missing colony facts.
    /// </summary>
    public static TechTier SelectTechTier(IReadOnlyList<ResearchProjectId> finished, TechLevel factionLevel) =>
        TechTierOf(finished, factionLevel).Tier;

    /// <summary>
    /// Names the condition that satisfied the selected tier (a research project, or "faction &lt;level&gt;" for
    /// a floor); empty for Camp.
    /// </summary>
    public static string TechTierEvidence(IReadOnlyList<ResearchProjectId> finished, TechLevel factionLevel) =>
        TechTierOf(finished, factionLevel).Evidence;

    private static (TechTier Tier, string Evidence) TechTierOf(IReadOnlyList<ResearchProjectId> finished, TechLevel level)
    {
        if (level == TechLevel.Undefined)
        {
            throw new RequiredFactException("player faction tech level");
        }
        var done = new HashSet<ResearchProjectId>(finished);
        // The floor: the tier the faction level satisfies outright.
        var tier = TechTier.Camp;
        var evidence = "";
        if (level >= TechLevel.Spacer)
        {
            tier = TechTier.Spacer;
        }
        else if (level >= TechLevel.Industrial)
        {
            tier = TechTier.Industrial;
        }
        else if (level >= TechLevel.Medieval)
        {
            tier = TechTier.Masonry;
        }
        if (tier > TechTier.Camp)
        {
            evidence = "faction " + level;
        }
        // Research raises the tier above the floor one condition at a time.
        while (tier < TechTier.Spacer)
        {
            var next = tier + 1;
            var raised = "";
            foreach (var id in Unlocks[next])
            {
                if (done.Contains(new ResearchProjectId(id)))
                {
                    raised = id;
                    break;
                }
            }
            if (raised.Length == 0)
            {
                break;
            }
            tier = next;
            evidence = raised;
        }
        return (tier, evidence);
    }
}
