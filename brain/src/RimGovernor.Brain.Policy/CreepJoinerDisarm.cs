namespace RimGovernor.Brain.Policy;

// Disarm arrested creepjoiners by installing and removing replacements for removable melee-tool body parts. The
// decompiled game rules require:
//
//   - A natural Hand or Jaw is offered no removal: Recipe_RemoveBodyPart offers a part only when it carries an
//     added part, is clean and its def spawns a thing on removal (neither body part def does), carries a bad
//     visible condition or is forceAlwaysRemovable.
//   - Recipe_InstallArtificialBodyPart lifts that: installing over a natural part is no violation on a pawn of
//     the bill doer's faction or of none (IsViolationOnPawn), and the added part then makes the part removable
//     (HasDirectlyAddedPartFor). Removing it leaves the part missing.
//   - A pawn's melee tools are the race's ThingDef.tools, each linked to a body part group; the tool whose group
//     is ensureLinkedBodyPartsGroupAlwaysUsable (the head) works whatever is missing. Every other tool needs a
//     part carrying its group, so a pawn missing all of those can only use that one.
//
// So the sites are, per race, the part that holds each other tool's group (the lowest part that contains every
// part carrying it), and the work per site is the cheapest install that is no violation, then its removal. Defs
// name nothing here: the sites come from the body and tools rows, the prices from the recipe rows.

/// <summary>One body part of a race whose absence silences a melee tool.</summary>
public sealed record DisarmSite
{
    /// <summary>The part's index in BodyDef.AllParts (the game's pre-order).</summary>
    public int Index { get; init; }

    /// <summary>The indexes of its ancestors.</summary>
    public EquatableArray<int> Ancestors { get; init; }
}

/// <summary>One surgery to queue on an arrested creepjoiner: an install of the cheapest prosthetic on a site, or the removal of the prosthetic on it.</summary>
public sealed record DisarmOrder(PawnId Pawn, string Recipe, int Part);

/// <summary>One review's disarming work: the surgeries to queue, one per prisoner.</summary>
public sealed record DisarmWork
{
    public EquatableArray<DisarmOrder> Orders { get; init; }
}

/// <summary>The def-sourced input of the disarming: the sites of each pawn kind's race body and the price of each install recipe's item.</summary>
public sealed record CreepJoinerDisarm
{
    public IReadOnlyDictionary<string, EquatableArray<DisarmSite>> Sites { get; init; } = new Dictionary<string, EquatableArray<DisarmSite>>();

    /// <summary>
    /// The market value of the items an install recipe consumes by name (its single-def ingredient slots); the
    /// medicine is priced by the operation. A recipe with no entry has no known price.
    /// </summary>
    public IReadOnlyDictionary<string, double> InstallValue { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// Finds the disarming the colony owes its arrested creepjoiners. A prisoner known to be a creepjoiner with
    /// no surgery bill queued is worked site by site: a site whose part (or an ancestor) is missing is done;
    /// one carrying an added part is ordered the removal the game offers; a natural one is ordered the cheapest
    /// install the game offers that is no violation, has an eligible doctor and stocked ingredients. Removals
    /// go first, then sites in index order; one bill per prisoner at a time. Unread facts leave a prisoner
    /// unworked.
    /// </summary>
    public DisarmWork Orders(IReadOnlyList<PrisonerFacts> prisoners)
    {
        var orders = new List<DisarmOrder>();
        foreach (var p in prisoners.OrderBy(x => x.Pawn))
        {
            if (!p.CreepJoiner.TryGetValue(out var joiner) || !joiner || p.Dead)
            {
                continue;
            }
            if (Order(p) is { } order)
            {
                orders.Add(order);
            }
        }
        return new DisarmWork { Orders = EquatableArray.From(orders) };
    }

    /// <summary>
    /// The next surgery on one creepjoiner prisoner: null when nothing is owed, a bill is already queued, or
    /// what is owed cannot be ordered.
    /// </summary>
    private DisarmOrder? Order(PrisonerFacts p)
    {
        if (!Sites.TryGetValue(p.Kind, out var sites) || sites.Count == 0)
        {
            return null;
        }
        if (p.QueuedSurgeries > 0)
        {
            return null;
        }
        var ops = p.Operations.Select(op => op.Operation).ToList();
        var missing = p.MissingParts;
        DisarmOrder? firstInstall = null;
        foreach (var site in sites)
        {
            if (CreepJoinerDisarmRules.SiteGone(site, missing))
            {
                continue;
            }
            if (CreepJoinerDisarmRules.RemovalOn(site, ops) is { } op)
            {
                if (!CreepJoinerDisarmRules.SurgeryQueueable(op))
                {
                    continue;
                }
                return new DisarmOrder(p.Pawn, op.Recipe, site.Index);
            }
            firstInstall ??= CheapestInstall(p.Pawn, site, ops);
        }
        return firstInstall;
    }

    /// <summary>The install on the site with the least item and medicine value among the offered ones that can be queued, else null.</summary>
    private DisarmOrder? CheapestInstall(PawnId pawn, DisarmSite site, IReadOnlyList<SurgeryOperation> ops)
    {
        DisarmOrder? best = null;
        var bestCost = 0.0;
        foreach (var op in ops)
        {
            var recipe = op.Recipe;
            if (op.Part?.Index != site.Index || op.Kind != SurgeryKind.Install || !CreepJoinerDisarmRules.SurgeryQueueable(op)
                || !InstallValue.TryGetValue(recipe, out var items))
            {
                continue;
            }
            var cost = items + op.MedicineMarketValue;
            if (best is null || cost < bestCost || (cost == bestCost && string.CompareOrdinal(recipe, best.Recipe) < 0))
            {
                best = new DisarmOrder(pawn, recipe, site.Index);
                bestCost = cost;
            }
        }
        return best;
    }
}

public static class CreepJoinerDisarmRules
{
    /// <summary>The site's part or one of its ancestors is missing.</summary>
    internal static bool SiteGone(DisarmSite site, IReadOnlyList<MissingBodyPart> missing)
    {
        foreach (var m in missing)
        {
            if (m.Part.Index == site.Index || site.Ancestors.Contains(m.Part.Index))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The removal the game offers of an added part on the site, or null.</summary>
    internal static SurgeryOperation? RemovalOn(DisarmSite site, IReadOnlyList<SurgeryOperation> ops)
    {
        foreach (var op in ops)
        {
            if (op.Part?.Index != site.Index || op.Kind != SurgeryKind.Amputate)
            {
                continue;
            }
            if (op.AddedPartHediff is { Length: > 0 })
            {
                return op;
            }
        }
        return null;
    }

    /// <summary>Whether an offered operation can be queued now: a known non-violation with an eligible doctor and its ingredients on the map.</summary>
    internal static bool SurgeryQueueable(SurgeryOperation op) => !op.Violation && op.EligibleDoctors > 0 && op.IngredientsOnMap;
}
