using RimGovernor.Contracts.Wire.Clock;
namespace RimGovernor.Brain.Policy;

public sealed record CombatSiteExit
{
    public EquatableArray<PawnId> Crew { get; init; }

    public EquatableArray<Cell> Cells { get; init; }
}

public static partial class CombatSiteExitRules
{
    internal static (CombatMemory Memory, List<CombatOrder> Orders, bool Ran) SiteExitTurn(
        CombatView view,
        StopEvent stop,
        CombatMemory m,
        IReadOnlyDictionary<PawnId, CombatPawnState> state,
        IReadOnlySet<PawnId> orderable)
    {
        if (view.SiteExit is not { } exit || exit.Cells.Count == 0)
        {
            return (m, [], false);
        }
        var failed = m.Tactic == CombatTactic.SiteExit || m.Scattered;
        if (stop.Kind == CombatEvent.Downed && exit.Crew.Contains(stop.Pawn))
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
                var at = pawn.Cell;
                if (CombatManhunterRules.Distance2(at, candidate) < CombatManhunterRules.Distance2(at, cell))
                {
                    cell = candidate;
                }
            }
            if (!available)
            {
                continue;
            }
            roles.Add(new CombatRole { Pawn = id, Post = new CombatPost.At(cell), Retreat = true });
            var order = new CombatOrder { Pawn = id, Kind = CombatOrderKind.Move, Cell = cell, Reason = CombatOrderReason.Retreat };
            // The roles recorded so far are the memory the doing check reads.
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
