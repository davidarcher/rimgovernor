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

    /// <summary>The part's BodyPartDef.</summary>
    public string Part { get; init; } = "";

    /// <summary>The indexes of its ancestors.</summary>
    public EquatableArray<int> Ancestors { get; init; }
}

/// <summary>One surgery to queue on an arrested creepjoiner: an install of the cheapest prosthetic on a site, or the removal of the prosthetic on it.</summary>
public sealed record DisarmOrder(PawnId Pawn, string Recipe, int Part, bool Remove = false);

/// <summary>One review's disarming work: the surgeries to queue, one per prisoner, and why a creepjoiner prisoner could not be worked on (plain English).</summary>
public sealed record DisarmWork
{
    public EquatableArray<DisarmOrder> Orders { get; init; }

    public EquatableArray<string> Waiting { get; init; }
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
    /// unworked with the reason in Waiting.
    /// </summary>
    public DisarmWork Orders(IReadOnlyList<PrisonerFacts> prisoners)
    {
        var orders = new List<DisarmOrder>();
        var waiting = new List<string>();
        foreach (var p in prisoners.OrderBy(x => x.Pawn))
        {
            if (!p.CreepJoiner.TryGetValue(out var joiner))
            {
                waiting.Add($"prisoner {p.Pawn} cannot be disarmed: its creepjoiner tracker could not be read");
                continue;
            }
            if (!joiner)
            {
                continue;
            }
            if (p.Dead.TryGetValue(out var dead) && dead)
            {
                continue;
            }
            var (order, why) = Order(p);
            if (why.Length != 0)
            {
                waiting.Add($"prisoner {p.Pawn} cannot be disarmed: {why}");
                continue;
            }
            if (order is not null)
            {
                orders.Add(order);
            }
        }
        return new DisarmWork { Orders = EquatableArray.From(orders), Waiting = EquatableArray.From(waiting) };
    }

    /// <summary>
    /// The next surgery on one creepjoiner prisoner: null with no reason when nothing is owed or a bill is
    /// already queued, a reason when something is owed and cannot be ordered.
    /// </summary>
    private (DisarmOrder? Order, string Why) Order(PrisonerFacts p)
    {
        if (!Sites.TryGetValue(p.Kind, out var sites) || sites.Count == 0)
        {
            return (null, $"the game gives pawn kind \"{p.Kind}\" no melee tool part to remove");
        }
        var qk = p.QueuedSurgeries.TryGetValue(out var queued);
        var ok = p.Operations.TryGetValue(out var ops);
        var mk = p.MissingParts.TryGetValue(out var missing);
        if (!qk || !ok || !mk)
        {
            return (null, "its surgery facts could not be read");
        }
        if (queued > 0)
        {
            return (null, "");
        }
        var installs = new List<DisarmOrder>();
        var why = "";
        var pawn = p.Pawn;
        foreach (var site in sites)
        {
            if (PolicyFunctions.SiteGone(site, missing!))
            {
                continue;
            }
            if (PolicyFunctions.RemovalOn(site, ops!) is { } op)
            {
                op.Recipe.TryGetValue(out var recipe);
                var reason = PolicyFunctions.SurgeryBlocked(op);
                if (reason.Length != 0)
                {
                    why = $"removing the {PolicyFunctions.AddedPartName(op)} on its {site.Part}: {reason}";
                    continue;
                }
                return (new DisarmOrder(pawn, recipe ?? "", site.Index, Remove: true), "");
            }
            var (install, installWhy) = CheapestInstall(pawn, site, ops!);
            if (installWhy.Length != 0)
            {
                why = installWhy;
                continue;
            }
            installs.Add(install!);
        }
        if (installs.Count > 0)
        {
            return (installs[0], "");
        }
        return (null, why);
    }

    /// <summary>The install on the site with the least item and medicine value among the offered ones that can be queued, else the reason.</summary>
    private (DisarmOrder? Order, string Why) CheapestInstall(PawnId pawn, DisarmSite site, IReadOnlyList<SurgeryOperation> ops)
    {
        DisarmOrder? best = null;
        var bestCost = 0.0;
        var why = $"the game offers no install on its {site.Part}";
        foreach (var op in ops)
        {
            if (!op.PartIndex.TryGetValue(out var index) || index != site.Index || op.Kind != SurgeryKind.Install)
            {
                continue;
            }
            if (!op.Recipe.TryGetValue(out var recipe))
            {
                continue;
            }
            var reason = PolicyFunctions.SurgeryBlocked(op);
            if (reason.Length != 0)
            {
                why = $"installing {recipe} on its {site.Part}: {reason}";
                continue;
            }
            if (!InstallValue.TryGetValue(recipe, out var items))
            {
                why = $"installing {recipe} on its {site.Part}: the recipe has no known price";
                continue;
            }
            op.MedicineValue.TryGetValue(out var medicine);
            var cost = items + medicine;
            if (best is null || cost < bestCost || (cost == bestCost && string.CompareOrdinal(recipe, best.Recipe) < 0))
            {
                best = new DisarmOrder(pawn, recipe, site.Index);
                bestCost = cost;
            }
        }
        return best is null ? (null, why) : (best, "");
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The site's part or one of its ancestors is missing.</summary>
    internal static bool SiteGone(DisarmSite site, IReadOnlyList<MissingPart> missing)
    {
        foreach (var m in missing)
        {
            if (m.PartIndex.TryGetValue(out var index) && (index == site.Index || site.Ancestors.Contains(index)))
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
            if (!op.PartIndex.TryGetValue(out var index) || index != site.Index || op.Kind != SurgeryKind.Amputate)
            {
                continue;
            }
            if (op.AddedPart.TryGetValue(out var added) && added.Length != 0 && op.Recipe.IsKnown)
            {
                return op;
            }
        }
        return null;
    }

    internal static string AddedPartName(SurgeryOperation op) => op.AddedPart.TryGetValue(out var name) ? name : "";

    /// <summary>Says why an offered operation cannot be queued now, empty when it can: a violation or an unread one, no eligible doctor, ingredients not on the map.</summary>
    internal static string SurgeryBlocked(SurgeryOperation op)
    {
        if (!op.Violation.TryGetValue(out var violation))
        {
            return "whether it is a violation could not be read";
        }
        if (violation)
        {
            return "the game counts it a violation";
        }
        if (!op.EligibleDoctors.TryGetValue(out var doctors) || doctors == 0)
        {
            return "no eligible doctor";
        }
        if (!op.IngredientsOnMap.TryGetValue(out var stocked) || !stocked)
        {
            return "its ingredients are not on the map";
        }
        return "";
    }
}
