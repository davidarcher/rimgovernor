using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public static partial class PsylinkRules
{
    // The royalty/psylink Need observes unmet psylink and level-up requirements for willing colonists below
    // MaxPsylinkLevel (PsylinkLevelUps; Deserter route excluded). Ordinary item use remains unsupported until shared
    // forecast admission owns its labor and consequences. Acquiring the neuroformer is the economy/resource Need's:
    // NeuroformerNeeds raises a stock floor of one, which the resource ladder meets with a production bill when the
    // recipe is available and a trade buy when a trader sells it. Psyfocus is an explicit unsupported
    // ordinary-control state until a shared meditation Method exists.

    /// <summary>
    /// The ThingDef that grants a psylink (or a level) to the colonist who uses it; psycast neurotrainers
    /// teach one psycast and need a psylink first, so only this def serves a colonist without one.
    /// </summary>
    public const string PsylinkNeuroformer = "PsychicAmplifier";

    /// <summary>
    /// The highest psylink level a neuroformer raises: the PsychicAmplifier hediff's maxSeverity, which
    /// CompUseEffect_InstallImplant.CanBeUsedBy refuses at.
    /// </summary>
    public const int MaxPsylinkLevel = 6;

    /// <summary>
    /// The colonists willing to take a psylink: available (alive, standing, undrafted, out of a mental
    /// state), read with a needs block, and with no psylink. A colonist's psylink facts are known only for a
    /// psycaster (WorkPawn.PsylinkLevel), so with the needs block read, an unknown level is none. Unknown
    /// without the pawn rows, and none when the game has no psylink neuroformer def. Sorted by id.
    /// </summary>
    public static Fact<EquatableArray<PawnId>> PsylinkCandidates(RoyaltyFacts facts, Fact<EquatableArray<WorkPawn>> pawns)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<EquatableArray<PawnId>>();
        }
        var output = new List<PawnId>();
        if (!facts.Neuroformers.ContainsKey(PsylinkRules.PsylinkNeuroformer))
        {
            return Known(EquatableArray.From(output));
        }
        foreach (var pawn in rows!)
        {
            var ak = pawn.Available.TryGetValue(out var available);
            var nk = pawn.Rest.TryGetValue(out _);
            var lk = pawn.PsylinkLevel.TryGetValue(out _);
            if (ak && available && nk && !lk)
            {
                output.Add(pawn.Id);
            }
        }
        output.Sort();
        return Known(EquatableArray.From(output));
    }

    /// <summary>
    /// The colonists a neuroformer already in stock raises a level: available, read with a needs block,
    /// holding a psylink below MaxPsylinkLevel and with no royal title in any faction (the Empire title caps
    /// the psylink level, and exceeding it breaks the rules). The royalty read's holdings carry every free
    /// colonist's titles, so a colonist with no holding is untitled. Unknown without the pawn rows, and none
    /// when the game has no psylink neuroformer def. Sorted by id. These colonists never raise
    /// NeuroformerNeeds: only existing stock serves them.
    /// </summary>
    public static Fact<EquatableArray<PawnId>> PsylinkLevelUps(RoyaltyFacts facts, Fact<EquatableArray<WorkPawn>> pawns)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<EquatableArray<PawnId>>();
        }
        var output = new List<PawnId>();
        if (!facts.Neuroformers.ContainsKey(PsylinkRules.PsylinkNeuroformer))
        {
            return Known(EquatableArray.From(output));
        }
        foreach (var pawn in rows!)
        {
            var ak = pawn.Available.TryGetValue(out var available);
            var nk = pawn.Rest.TryGetValue(out _);
            var lk = pawn.PsylinkLevel.TryGetValue(out var level);
            if (ak && available && nk && lk && level < PsylinkRules.MaxPsylinkLevel && !Titled(facts, pawn.Id))
            {
                output.Add(pawn.Id);
            }
        }
        output.Sort();
        return Known(EquatableArray.From(output));
    }

    /// <summary>Reports whether the colonist holds a royal title with any faction.</summary>
    private static bool Titled(RoyaltyFacts facts, PawnId pawn) =>
        facts.Holders.TryGetValue(pawn, out var holdings) && holdings.Any(h => h.Title is not null);

    /// <summary>
    /// Adds a stock floor of one psylink neuroformer to needs while a candidate waits, none is held and the
    /// game can supply one (an available recipe or a trader selling it). Unknown candidates add nothing: the
    /// colony never buys against an unread census. candidates are the first-psylink colonists only, never
    /// PsylinkLevelUps.
    /// </summary>
    public static IReadOnlyDictionary<Resource, long> NeuroformerNeeds(IReadOnlyDictionary<Resource, long> needs, RoyaltyFacts facts, Fact<EquatableArray<PawnId>> candidates)
    {
        if (!candidates.TryGetValue(out var who) || who.Count == 0)
        {
            return needs;
        }
        var ok = facts.Neuroformers.TryGetValue(PsylinkRules.PsylinkNeuroformer, out var stock);
        if (!ok)
        {
            return needs;
        }
        if (stock!.Held > 0 || (!stock.Craftable && !stock.Tradeable))
        {
            return needs;
        }
        return ResourceRules.ResourceConcernTargets(needs, new Dictionary<Resource, long> { [new Resource(PsylinkRules.PsylinkNeuroformer)] = 1 });
    }

}
