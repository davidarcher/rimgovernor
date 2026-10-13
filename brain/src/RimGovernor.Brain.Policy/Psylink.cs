using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>
    /// MaintainPsylink gives willing colonists a psylink and raises untitled psycasters below MaxPsylinkLevel
    /// from neuroformers already in stock (PsylinkLevelUps; Deserter route excluded): the psylink neuroformer
    /// is used by the colonist on itself (UseItem with the pawn as its own target). Acquiring the neuroformer
    /// is MaintainResource's: NeuroformerNeeds raises a stock floor of one, which the resource ladder meets
    /// with a production bill when the recipe is available and a trade buy when a trader sells it. Psyfocus is
    /// kept at target by the meditation schedule (PlanSchedulesHeld), not by this goal.
    /// </summary>
    public static readonly ConcernId MaintainPsylink = new("MaintainPsylink");

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
}

/// <summary>One colonist using one neuroformer item on itself.</summary>
public sealed record PsylinkUse(PawnId Pawn, string Item);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The colonists willing to take a psylink: available (alive, standing, undrafted, out of a mental
    /// state), read with a needs block, and with no psylink. A colonist's psylink facts are known only for a
    /// psycaster (WorkPawn.PsylinkLevel), so with the needs block read, an unknown level is none. Unknown
    /// without the royalty read or the pawn rows, and none when the game has no psylink neuroformer def.
    /// Sorted by id.
    /// </summary>
    public static Fact<EquatableArray<PawnId>> PsylinkCandidates(Fact<RoyaltyFacts> royalty, Fact<EquatableArray<WorkPawn>> pawns)
    {
        var rk = royalty.TryGetValue(out var facts);
        var pk = pawns.TryGetValue(out var rows);
        if (!rk || !pk)
        {
            return Unknown<EquatableArray<PawnId>>();
        }
        var output = new List<PawnId>();
        if (!facts!.Neuroformers.ContainsKey(PolicyConstants.PsylinkNeuroformer))
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
    /// colonist's titles, so a colonist with no holding is untitled. Unknown without the royalty read or the
    /// pawn rows, and none when the game has no psylink neuroformer def. Sorted by id. These colonists never
    /// raise NeuroformerNeeds: only existing stock serves them.
    /// </summary>
    public static Fact<EquatableArray<PawnId>> PsylinkLevelUps(Fact<RoyaltyFacts> royalty, Fact<EquatableArray<WorkPawn>> pawns)
    {
        var rk = royalty.TryGetValue(out var facts);
        var pk = pawns.TryGetValue(out var rows);
        if (!rk || !pk)
        {
            return Unknown<EquatableArray<PawnId>>();
        }
        var output = new List<PawnId>();
        if (!facts!.Neuroformers.ContainsKey(PolicyConstants.PsylinkNeuroformer))
        {
            return Known(EquatableArray.From(output));
        }
        foreach (var pawn in rows!)
        {
            var ak = pawn.Available.TryGetValue(out var available);
            var nk = pawn.Rest.TryGetValue(out _);
            var lk = pawn.PsylinkLevel.TryGetValue(out var level);
            if (ak && available && nk && lk && level < PolicyConstants.MaxPsylinkLevel && !Titled(facts, pawn.Id))
            {
                output.Add(pawn.Id);
            }
        }
        output.Sort();
        return Known(EquatableArray.From(output));
    }

    /// <summary>Reports whether the colonist holds a royal title with any faction.</summary>
    private static bool Titled(RoyaltyFacts facts, PawnId pawn) =>
        facts.Holders.TryGetValue(pawn, out var holdings) && holdings.Any(h => h.Title.Length != 0);

    /// <summary>
    /// Adds a stock floor of one psylink neuroformer to needs while a candidate waits, none is held and the
    /// game can supply one (an available recipe or a trader selling it). Unknown stock or candidates add
    /// nothing: the colony never buys against an unread census. candidates are the first-psylink colonists
    /// only, never PsylinkLevelUps.
    /// </summary>
    public static IReadOnlyDictionary<Resource, long> NeuroformerNeeds(IReadOnlyDictionary<Resource, long> needs, Fact<RoyaltyFacts> royalty, Fact<EquatableArray<PawnId>> candidates)
    {
        var rk = royalty.TryGetValue(out var facts);
        var ck = candidates.TryGetValue(out var who);
        if (!rk || !ck || who!.Count == 0)
        {
            return needs;
        }
        var ok = facts!.Neuroformers.TryGetValue(PolicyConstants.PsylinkNeuroformer, out var stock);
        if (!ok)
        {
            return needs;
        }
        var hk = stock!.Held.TryGetValue(out var held);
        stock.Craftable.TryGetValue(out var craftable);
        stock.Tradeable.TryGetValue(out var tradeable);
        if (!hk || held > 0 || (!craftable && !tradeable))
        {
            return needs;
        }
        return ResourceConcernTargets(needs, new Dictionary<Resource, long> { [new Resource(PolicyConstants.PsylinkNeuroformer)] = 1 });
    }

    /// <summary>
    /// The use the colony owes now: the first candidate (a colonist with no psylink yet) with the first held
    /// neuroformer item, else the first level-up colonist. items are the unforbidden item ids a live read
    /// found; the stock count in the royalty read lags it. One use per call: a second colonist is served once
    /// the first one's psylink read catches up. Null when nothing is owed.
    /// </summary>
    public static PsylinkUse? NextPsylinkUse(IReadOnlyList<PawnId> candidates, IReadOnlyList<PawnId> levelUps, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return null;
        }
        var item = items.Min(StringComparer.Ordinal)!;
        if (candidates.Count > 0)
        {
            return new PsylinkUse(candidates.Min(), item);
        }
        if (levelUps.Count > 0)
        {
            return new PsylinkUse(levelUps.Min(), item);
        }
        return null;
    }

    /// <summary>
    /// Measures MaintainPsylink: a use is owed while a candidate or a level-up colonist and a held neuroformer
    /// item both exist. Unknown while the candidates or level-ups are unknown or a colonist waits on an unread
    /// item list; known false with no one waiting.
    /// </summary>
    public static Fact<bool> PsylinkOwed(Fact<EquatableArray<PawnId>> candidates, Fact<EquatableArray<PawnId>> levelUps, Fact<EquatableArray<string>> items)
    {
        var ck = candidates.TryGetValue(out var first);
        var uk = levelUps.TryGetValue(out var up);
        if (!ck || !uk)
        {
            return Unknown<bool>();
        }
        if (first!.Count + up!.Count == 0)
        {
            return Known(false);
        }
        if (!items.TryGetValue(out var held))
        {
            return Unknown<bool>();
        }
        return Known(held.Count > 0);
    }
}
