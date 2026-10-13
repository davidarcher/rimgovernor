namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the colony owes its held entities now: the entities whose extract flag is to be set, by pawn ID, and
/// the unread facts that kept the rule from deciding. (Go <c>BioferriteHarvest</c>.)
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
public static partial class PolicyFunctions
{
    /// <summary>Go <c>ResearchBioferriteExtraction</c>: ResearchProjectDefOf.BioferriteExtraction.</summary>
    public static readonly ResearchProjectId ResearchBioferriteExtraction = new("BioferriteExtraction");

    /// <summary>JobDriver_ExtractBioferrite.BioferriteModifier: an extraction yields floor(BioferritePerDay * 4).</summary>
    private const int BioferriteYieldFactor = 4;

    /// <summary>Go <c>BioferriteYield</c>: the bioferrite one extraction yields at perDay.</summary>
    public static int BioferriteYield(double perDay) => (int)Math.Floor(perDay * BioferriteYieldFactor);

    /// <summary>
    /// Go <c>BioferriteHarvestOwed</c>: decides every held, living entity. Research that is not finished owes
    /// nothing; research that is unread is an issue whenever an entity is held.
    /// </summary>
    public static BioferriteHarvest BioferriteHarvestOwed(ContainmentPlanning p, Fact<ResearchFacts> research)
    {
        if (!p.Entities.TryGetValue(out var entities))
        {
            return new BioferriteHarvest();
        }
        var issues = new List<string>();
        var held = new List<CapturableEntity>();
        foreach (var e in entities)
        {
            var dk = e.Dead.TryGetValue(out var dead);
            var hk = e.Held.TryGetValue(out var isHeld);
            if (dk && dead)
            {
                continue;
            }
            if (!hk)
            {
                issues.Add($"whether a platform holds entity {e.Pawn} is unread");
            }
            else if (isHeld)
            {
                held.Add(e);
            }
        }
        if (held.Count == 0)
        {
            return new BioferriteHarvest { Issues = issues };
        }
        if (!research.TryGetValue(out var facts))
        {
            issues.Add("whether BioferriteExtraction is researched is unread");
            return new BioferriteHarvest { Issues = issues };
        }
        if (!facts.Finished.Contains(ResearchBioferriteExtraction))
        {
            return new BioferriteHarvest { Issues = issues };
        }
        var enable = new List<Domain.PawnId>();
        foreach (var e in held)
        {
            var (on, reason) = BioferriteEnable(e);
            if (reason.Length != 0)
            {
                issues.Add(reason);
            }
            else if (on)
            {
                enable.Add(e.Pawn);
            }
        }
        enable.Sort();
        return new BioferriteHarvest { Enable = enable, Issues = issues };
    }

    /// <summary>Whether the held entity's flag is to be set; reason names the unread fact otherwise.</summary>
    private static (bool Enable, string Reason) BioferriteEnable(CapturableEntity e)
    {
        (bool, string) Unread(string what) => (false, $"{what} of entity {e.Pawn} is unread");
        if (!e.Mode.TryGetValue(out var mode))
        {
            return Unread("the containment mode");
        }
        if (!e.ExtractBioferrite.TryGetValue(out var flag))
        {
            return Unread("the extract bioferrite flag");
        }
        if (!e.HarvesterAttached.TryGetValue(out var harvester))
        {
            return Unread("whether a bioferrite harvester is attached");
        }
        if (!e.BioferritePerDay.TryGetValue(out var perDay))
        {
            return Unread("the bioferrite production");
        }
        if (mode == ContainmentMode.Release || flag || harvester)
        {
            return (false, "");
        }
        return (BioferriteYield(perDay) >= 1, "");
    }

    /// <summary>
    /// Whether a held entity's extract flag is to be set: a standing work for MaintainPopulation, whose custody
    /// step carries it.
    /// </summary>
    internal static bool EntityBioferriteOwed(ContainmentPlanning p, Fact<ResearchFacts> research) =>
        BioferriteHarvestOwed(p, research).Enable.Count > 0;
}
