namespace RimGovernor.Brain.Policy;

public sealed record CombatSiteExit
{
    public EquatableArray<PawnId> Crew { get; init; }

    public EquatableArray<Cell> Cells { get; init; }
}

public static partial class PolicyFunctions
{
    internal static (CombatMemory Memory, List<CombatOrder> Orders, bool Ran) SiteExitTurn(
        CombatView view,
        StopEvent stop,
        CombatMemory m,
        IReadOnlyDictionary<PawnId, CombatPawnState> state,
        IReadOnlySet<PawnId> orderable)
    {
        if (!view.SiteExit.TryGetValue(out var exit) || exit.Cells.Count == 0)
        {
            return (m, [], false);
        }
        var failed = m.Tactic == CombatTactic.SiteExit || m.Scattered;
        if (stop.Kind == CombatStopKind.Downed && exit.Crew.Contains(stop.Pawn))
        {
            failed = true;
        }
        if (!failed)
        {
            return (m, [], false);
        }
        m = m with { Tactic = CombatTactic.SiteExit };
        var roles = new List<CombatRole>();
        var orders = new List<CombatOrder>();
        foreach (var id in exit.Crew)
        {
            if (!state.TryGetValue(id, out var pawn) || pawn.Dead || pawn.Downed)
            {
                continue;
            }
            var cell = exit.Cells[0];
            var available = false;
            foreach (var candidate in exit.Cells)
            {
                if (m.Unreachable.Contains(candidate))
                {
                    continue;
                }
                if (!available)
                {
                    cell = candidate;
                    available = true;
                    continue;
                }
                if (pawn.Cell.TryGetValue(out var at) && Distance2(at, candidate) < Distance2(at, cell))
                {
                    cell = candidate;
                }
            }
            if (!available)
            {
                continue;
            }
            roles.Add(new CombatRole { Pawn = id, Cell = cell, Retreat = true });
            var order = new CombatOrder { Pawn = id, Kind = CombatOrderKind.Move, Cell = cell, Reason = CombatOrderReason.Retreat };
            // The roles recorded so far are the memory the doing check reads, as Go's m.Roles was.
            m = m with { Roles = EquatableArray.From(roles) };
            if (orderable.Contains(id) && !m.Doing(order, pawn))
            {
                orders.Add(order);
                m = m.Issue(order, view.Tick);
            }
        }
        return (m with { Roles = EquatableArray.From(roles) }, orders, true);
    }
}
