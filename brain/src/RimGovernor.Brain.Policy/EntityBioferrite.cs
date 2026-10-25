namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the colony owes its held entities now: the entities whose extract flag is to be set, by pawn ID, and
/// the unread facts that kept the rule from deciding.
/// </summary>
public sealed record BioferriteHarvest
{
    public IReadOnlyList<Domain.PawnId> Enable { get; init; } = [];

    public IReadOnlyList<string> Issues { get; init; } = [];
}

// The bioferrite harvest rule: a held entity's extract-bioferrite flag is set when extracting pays, and the
// game's own Doctor work giver (WorkGiver_ExtractBioferrite) then runs the extraction job. Every rule is the
// game's, from the decompile; the flag is not set while an extraction is cooling down, an extraction yields
// floor(perDay * 4) bioferrite, a powered harvester forces the flag false, an entity in Release mode is
// skipped, and the order is unlocked by the BioferriteExtraction research. An unread fact is a loud issue,
// never a guess.
public static class EntityBioferriteRules
{
    /// <summary>ResearchProjectDefOf.BioferriteExtraction.</summary>
    public static readonly ResearchProjectId ResearchBioferriteExtraction = new("BioferriteExtraction");

    /// <summary>JobDriver_ExtractBioferrite.BioferriteModifier: an extraction yields floor(BioferritePerDay * 4).</summary>
    private const int BioferriteYieldFactor = 4;

    /// <summary>The bioferrite one extraction yields at perDay.</summary>
    public static int BioferriteYield(double perDay) => (int)Math.Floor(perDay * BioferriteYieldFactor);

    /// <summary>
    /// Decides every held, living entity. Research that is not finished owes
    /// nothing; research that is unread is an issue whenever an entity is held.
    /// </summary>
    public static BioferriteHarvest BioferriteHarvestOwed(ContainmentPlanning p, ResearchFacts? facts)
    {
        var issues = new List<string>();
        var held = new List<(Domain.PawnId Pawn, RimGovernor.Brain.GameFacts.Observations.HeldState Target)>();
        foreach (var e in p.Entities)
        {
            if (!e.Dead && e.Target is { Held: true } target)
            {
                held.Add((e.Pawn, target));
            }
        }
        if (held.Count == 0)
        {
            return new BioferriteHarvest { Issues = issues };
        }
        if (facts is null)
        {
            issues.Add("whether BioferriteExtraction is researched is unread");
            return new BioferriteHarvest { Issues = issues };
        }
        if (!facts.Finished.Contains(ResearchBioferriteExtraction))
        {
            return new BioferriteHarvest { Issues = issues };
        }
        var enable = new List<Domain.PawnId>();
        foreach (var (pawn, target) in held)
        {
            if (BioferriteEnable(target))
            {
                enable.Add(pawn);
            }
        }
        enable.Sort();
        return new BioferriteHarvest { Enable = enable, Issues = issues };
    }

    /// <summary>Whether the held entity's flag is to be set.</summary>
    private static bool BioferriteEnable(RimGovernor.Brain.GameFacts.Observations.HeldState t) =>
        t.Mode != EntityContainmentModeKind.Release && !t.ExtractBioferrite && !t.HarvesterAttached && BioferriteYield(t.BioferritePerDay) >= 1;
}
