using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Reads live pawn positions and jobs for the arrival hold (Go <c>ArrivalPawns</c>).</summary>
public interface IArrivalPawns
{
    Task<PbObservations.ListPawnsReply> ReadCombatPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> pawnIds, CancellationToken ct);
}

/// <summary>One completed move an undispatched action depends on.</summary>
internal readonly record struct ArrivalMove(PlanId Plan, DomainAction Action);

public sealed partial class Worker
{
    /// <summary>
    /// Names the undispatched actions that depend on a completed move whose pawn does not yet stand on its
    /// destination. A move's receipt only says the order was given; the shrine plan's breach or opening waits here,
    /// in the worker, until every mover has arrived. A drafted pawn idle away from its cell has lost its order, so
    /// its move is sent again (the intent is idempotent). An unreadable position holds.
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
                if (required.Action.Movement is not { } m || required.View.Stage != Stage.Completed)
                {
                    continue;
                }
                if (!gated.TryGetValue(v.Action, out var list))
                {
                    gated[v.Action] = list = [];
                }
                list.Add(new ArrivalMove(plan.Spec.Id, required.Action));
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
        var (rows, tick) = await ArrivalRowsAsync(current, ids, ct).ConfigureAwait(false);
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
                if (BreakDispatch.BreakCell(row).TryGetValue(out var at) && at == m.Destination)
                {
                    continue;
                }
                held.Add(waiting);
                if (row.Drafted.GetValueOrDefault() && (row.Job?.DefName ?? "") != "Goto" && resent.Add(move.Action.Id) && _config.Moves is not null)
                {
                    await ResendMoveAsync(current, move, tick, ct).ConfigureAwait(false);
                }
            }
        }
        return held;
    }

    private async Task<(Dictionary<string, PbObservations.PawnState> Rows, long Tick)> ArrivalRowsAsync(GenerationSnapshot current, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var rows = new Dictionary<string, PbObservations.PawnState>(StringComparer.Ordinal);
        if (_config.Pawns is null)
        {
            return (rows, 0);
        }
        PbObservations.ListPawnsReply reply;
        try
        {
            reply = await _config.Pawns.ReadCombatPawnsAsync(Boundary.Identity(current), ids, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (rows, 0);
        }
        var observed = reply.Outcome?.Observed;
        if (observed is null)
        {
            return (rows, 0);
        }
        try
        {
            _ = Boundary.Context(observed.Context, current);
        }
        catch (DomainException)
        {
            return (rows, 0);
        }
        foreach (var row in observed.Pawns)
        {
            rows[row.Pawn?.Id ?? ""] = row;
        }
        return (rows, observed.Context?.Tick ?? 0);
    }

    private async Task ResendMoveAsync(GenerationSnapshot current, ArrivalMove move, long tick, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{move.Plan}/{move.Action.Id}/{tick}");
        // A failed resend is retried at the next arrival read.
        try
        {
            var action = BridgeApi.IntentAction(key, move.Action);
            _ = await _config.Moves!.ApplyAsync(Boundary.Identity(current), [action], ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // intentionally dropped
        }
    }
}
