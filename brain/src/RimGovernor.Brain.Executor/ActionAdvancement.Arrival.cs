using System.Diagnostics;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Telemetry;
using Decision = RimGovernor.Brain.Telemetry.Decision;

namespace RimGovernor.Brain.Executor;

/// <summary>One move an undispatched action depends on.</summary>
internal readonly record struct ArrivalMove(PlanId Plan, DomainAction Action, ProgressView View);

public sealed partial class ActionAdvancement
{
    /// <summary>
    /// Names the undispatched actions that depend on a move whose pawn does not yet stand on its
    /// destination. A move's receipt only says the order was given; a plan's dependent actions wait here,
    /// in Hands, until every mover has arrived. A drafted pawn idle away from its cell has lost its order, so
    /// its completed move is reasserted through the journal. An unreadable position holds.
    /// </summary>
    internal async Task<HashSet<ActionId>> ArrivalHoldsAsync(GenerationSnapshot current, IReadOnlyList<PlanState> plans, CancellationToken ct)
    {
        var held = new HashSet<ActionId>();
        var gated = new Dictionary<ActionId, List<ArrivalMove>>();
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var plan in plans)
        {
            if (plan.Retired)
            {
                continue;
            }
            var byId = new Dictionary<ActionId, Progress>();
            foreach (var p in plan.Progress)
            {
                byId[p.View.Action] = p;
            }
            foreach (var dependency in plan.Spec.Dependencies)
            {
                if (!byId.TryGetValue(dependency.Action, out var waiting) || !byId.TryGetValue(dependency.Requires, out var required))
                {
                    continue;
                }
                var v = waiting.View;
                if (v.Unresolved || (v.Stage != Stage.Pending && v.Stage != Stage.Prepared))
                {
                    continue;
                }
                if (required.Action.Movement is not { } m)
                {
                    continue;
                }
                if (!gated.TryGetValue(v.Action, out var list))
                {
                    gated[v.Action] = list = [];
                }
                list.Add(new ArrivalMove(plan.Spec.Id, required.Action, required.View));
                if (pawns.Add(m.Pawn.Value))
                {
                    ids.Add(m.Pawn.Value);
                }
            }
        }
        if (gated.Count == 0)
        {
            return held;
        }
        var (rows, tick) = await ActionObservations.ReadPawnsAsync(_config.Pawns, current, ids, ct).ConfigureAwait(false);
        var resent = new HashSet<ActionId>();
        // Waiting actions are visited in id order so the resend order does not depend on dictionary order.
        foreach (var (waiting, moves) in gated.OrderBy(kv => kv.Key))
        {
            foreach (var move in moves)
            {
                var m = move.Action.Movement!;
                if (!rows.TryGetValue(m.Pawn.Value, out var row))
                {
                    held.Add(waiting);
                    continue;
                }
                if (BreakDispatch.TryBreakCell(row, out var at) && at == m.Destination)
                {
                    continue;
                }
                held.Add(waiting);
                if (move.View.Stage == Stage.Completed && row.Drafted && (row.Job.DefName ?? "") != "Goto" && resent.Add(move.Action.Id))
                {
                    await ResendMoveAsync(move, tick, ct).ConfigureAwait(false);
                }
            }
        }
        return held;
    }

    private async Task ResendMoveAsync(ArrivalMove move, long tick, CancellationToken ct)
    {
        // Reassertion shares the original action and records a new attempt; uncertainty is never discarded.
        var began = Stopwatch.GetTimestamp();
        var tally = new ReadTally();
        using var reads = tally.Begin();
        var item = await _config.Session.ReassertMovementAsync(move.Plan, move.Action.Id, new Tick(tick), ct).ConfigureAwait(false);
        _config.RecordOutcome(ActionKind.Movement, item.Result, item.Error);
        if (item.Result.NativeCalled)
        {
            _config.Written?.Invoke(ActionKind.Movement);
        }
        var after = item.Result.Progress?.View ?? move.View;
        WorkerDispatchRow(tally, move.View, after, item.Result, _config.ClockRunning?.Invoke() == true,
            WorkerHeldStale(after, item.Result, item.Error), item.Error, true, 0, Stopwatch.GetElapsedTime(began));
        ct.ThrowIfCancellationRequested();
    }
}
