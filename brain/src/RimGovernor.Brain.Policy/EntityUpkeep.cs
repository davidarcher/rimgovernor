namespace RimGovernor.Brain.Policy;

/// <summary>
/// A containment upkeep fact the colony cannot act on: Reason says in plain English what is unread or cannot be
/// ordered. Cell is the door the issue is about, or the default cell for a holder level issue. (Go <c>UpkeepIssue</c>.)
/// </summary>
public sealed record UpkeepIssue(string Reason, Cell Cell = default);

/// <summary>
/// What the colony owes its held entities now: doors to close and issues to report. Closes are by cell, sorted.
/// (Go <c>ContainmentUpkeep</c>.)
/// </summary>
public sealed record ContainmentUpkeep
{
    public IReadOnlyList<Cell> CloseDoors { get; init; } = [];

    public IReadOnlyList<UpkeepIssue> Issues { get; init; } = [];
}

// Containment upkeep: keep a held entity's cell closed and its captive tended. The rules are the game's own,
// read from the Building_Door and StatWorker_ContainmentStrength decompile: a door open 600 ticks is breached
// and zeroes the door term of the room; a door stays open only when held open or something stands in the
// doorway, and the only write that closes a door is the combat door CLOSE order. A held pawn with a hediff that
// needs tending is tended like any patient. Only a holder that holds a pawn is kept. An unread fact is a loud
// issue, never a pass.
public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>ContainmentDoorUpkeep</c>: reads every holder that holds a pawn. A door held open is closed; a
    /// door that is breached but is not held open and is blocked open is an issue (an order cannot clear what
    /// stands in it); an unread door fact is an issue. A door open and not held open closes by itself and is
    /// left alone.
    /// </summary>
    public static ContainmentUpkeep ContainmentDoorUpkeep(ContainmentPlanning p)
    {
        if (!p.Holders.TryGetValue(out var holders))
        {
            return new ContainmentUpkeep();
        }
        var close = new List<Cell>();
        var issues = new List<UpkeepIssue>();
        var seen = new HashSet<Cell>();
        foreach (var h in holders)
        {
            if (h.HeldPawn.Length == 0)
            {
                continue;
            }
            if (!h.Doors.TryGetValue(out var doors))
            {
                issues.Add(new UpkeepIssue($"the doors of the cell holding {h.HeldPawn} are unread"));
                continue;
            }
            foreach (var d in doors)
            {
                if (!seen.Add(d.Cell))
                {
                    continue;
                }
                var hk = d.HoldOpen.TryGetValue(out var hold);
                var bk = d.Breached.TryGetValue(out var breached);
                var ek = d.BlockedOpen.TryGetValue(out var blocked);
                if (!hk)
                {
                    issues.Add(new UpkeepIssue("whether the cell door is held open is unread", d.Cell));
                }
                else if (hold)
                {
                    close.Add(d.Cell);
                }
                else if (!bk)
                {
                    issues.Add(new UpkeepIssue("whether the cell door is breached is unread", d.Cell));
                }
                else if (breached && !ek)
                {
                    issues.Add(new UpkeepIssue("the cell door is breached and whether it is blocked open is unread", d.Cell));
                }
                else if (breached && blocked)
                {
                    issues.Add(new UpkeepIssue("the cell door is breached and blocked open by a thing in the doorway; no order clears that", d.Cell));
                }
            }
        }
        return new ContainmentUpkeep { CloseDoors = [.. close.OrderBy(c => c.Z).ThenBy(c => c.X)], Issues = issues };
    }

    /// <summary>
    /// Whether a held entity's cell door is to be closed: a standing work for MaintainPopulation, whose custody
    /// step carries it.
    /// </summary>
    internal static bool EntityDoorOwed(ContainmentPlanning p) => ContainmentDoorUpkeep(p).CloseDoors.Count > 0;

    /// <summary>Go <c>ContainmentDoorTarget</c>: the first cell door to close, by position; null when none.</summary>
    public static Cell? ContainmentDoorTarget(ContainmentPlanning p)
    {
        var doors = ContainmentDoorUpkeep(p).CloseDoors;
        return doors.Count == 0 ? null : doors[0];
    }

    /// <summary>
    /// Go <c>EntityTendTarget</c>: the held entity to tend next: one that is alive, held and read as needing
    /// tend, a bleeding one first, then by pawn ID. An entity whose health facts are unread is skipped; null
    /// when none.
    /// </summary>
    public static Domain.PawnId? EntityTendTarget(ContainmentPlanning p)
    {
        if (!p.Entities.TryGetValue(out var entities))
        {
            return null;
        }
        Domain.PawnId best = default;
        var bestBleeding = false;
        foreach (var e in entities)
        {
            var dk = e.Dead.TryGetValue(out var dead);
            var hk = e.Held.TryGetValue(out var held);
            var nk = e.NeedsTend.TryGetValue(out var needs);
            var wk = e.Downed.TryGetValue(out var downed);
            if (!dk || dead || !hk || !held || !nk || !needs)
            {
                continue;
            }
            if (!wk || !downed)
            {
                // SelectTend tends a patient only where they lie (downed or in a bed, tendable), and an entity is
                // in no bed: a standing entity is no deficit the colony can work, so none is owed.
                continue;
            }
            var bleeding = e.Bleeding.ValueOrDefault;
            if (best.IsEmpty || (bleeding && !bestBleeding) || (bleeding == bestBleeding && e.Pawn < best))
            {
                best = e.Pawn;
                bestBleeding = bleeding;
            }
        }
        return best.IsEmpty ? null : best;
    }

    /// <summary>Whether a held entity needs tending.</summary>
    internal static bool EntityTendOwed(ContainmentPlanning p) => EntityTendTarget(p) is not null;
}
