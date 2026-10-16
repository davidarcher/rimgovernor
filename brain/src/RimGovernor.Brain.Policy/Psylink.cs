using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>
    /// MaintainPsylink observes unmet psylink and level-up requirements for willing colonists below MaxPsylinkLevel
    /// (PsylinkLevelUps; Deserter route excluded). Ordinary item use remains unsupported until shared forecast
    /// admission owns its labor and consequences. Acquiring the neuroformer
    /// is the economy/resource Need's: NeuroformerNeeds raises a stock floor of one, which the resource ladder meets
    /// with a production bill when the recipe is available and a trade buy when a trader sells it. Psyfocus is
    /// an explicit unsupported ordinary-control state until a shared meditation Method exists.
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
