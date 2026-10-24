using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Releasing a breaking pawn's work and fencing new dispatch near an aggressive break.</summary>
internal static class BreakDispatch
{
    /// <summary>The cell a pawn stands on; false for a pawn on no map (caravan, transport, the world).</summary>
    public static bool TryBreakCell(PbObservations.PawnState row, out Cell cell)
    {
        if (row.Pawn.Place is PbObservations.EntityPlace.OnMap { Cell: var p })
        {
            cell = new Cell(p.X, p.Z);
            return true;
        }

        cell = default;
        return false;
    }

    /// <summary>
    /// Releases the breaking pawn's action claims through ordinary cancellation; dispatched attempts remain
    /// reconcilable and owned drafts retain cleanup. A subdue order, applied or not, is cancelled once its victim
    /// is no longer in an aggressive break (downed, recovered or gone): that ends the hold on its draft, the
    /// cleanup releases it and the plan retires.
    /// </summary>
    public static async Task ReleaseBreakWorkAsync(JournalStore journal, GenerationSnapshot current, EmergencyFacts f, IReadOnlyList<PlanState> plans, CancellationToken ct)
    {
        var broken = new HashSet<PawnId>();
        var aggressive = new HashSet<PawnId>();
        foreach (var p in f.Colonists)
        {
            if (BreakResponseRules.AggressiveBreak(p))
            {
                aggressive.Add(p.Id);
            }
            if (p.MentalState is not null)
            {
                broken.Add(p.Id);
            }
        }
        var complete = f.AllSeen;
        foreach (var plan in plans)
        {
            if (plan.Retired)
            {
                continue;
            }
            if (broken.Count == 0 && !plan.Spec.Actions.Any(action => action.Kind == ActionKind.Subdue))
            {
                continue;
            }
            var sites = await journal.DispatchSitesAsync(plan.Spec.Id, ct).ConfigureAwait(false);
            foreach (var p in plan.Progress)
            {
                var v = p.View;
                if (p.Action.Subdue is { } m && complete && !aggressive.Contains((PawnId)m.Target) && !v.Unresolved
                    && v.Stage is Stage.Pending or Stage.Prepared or Stage.Completed)
                {
                    await journal.CancelAsync(plan.Spec.Id, v.Action, ct).ConfigureAwait(false);
                    continue;
                }
                var worker = sites.TryGetValue(v.Action, out var site) ? site.Worker : default;
                if (!broken.Contains((PawnId)worker) || v.Stage is Stage.Completed or Stage.Cancelled or Stage.Unsuccessful)
                {
                    continue;
                }
                if (v.Attempt.Value > 0 && !v.Snapshot.SameWorld(current))
                {
                    continue;
                }
                await journal.CancelAsync(plan.Spec.Id, v.Action, ct).ConfigureAwait(false);
            }
        }
    }
}

public sealed partial class Worker
{
    /// <summary>
    /// Reads one fresh census for the worker step. Cleanup and uncertain outcomes are never fenced: only new
    /// dispatch is subject to the exclusion radius.
    /// </summary>
    internal async Task<HashSet<ActionId>> BreakDispatchHoldsAsync(GenerationSnapshot current, IReadOnlyList<PlanState> plans, CancellationToken ct)
    {
        var held = new HashSet<ActionId>();
        if (_config.BreakSource is null)
        {
            return held;
        }
        var census = await _config.BreakSource.ReadEmergencyAsync(Boundary.Identity(current), ct).ConfigureAwait(false);
        _ = Boundary.Context(census.Context, current);
        if (!census.Facts.AllSeen)
        {
            throw new ControlException("breakDispatchHolds: !known || !complete");
        }
        var ids = new List<string>();
        var broken = new HashSet<PawnId>();
        var aggressive = new List<PawnId>();
        foreach (var p in census.Facts.Colonists)
        {
            ids.Add(p.Id.Value);
            if (p.MentalState is not null)
            {
                broken.Add(p.Id);
            }
            if (BreakResponseRules.AggressiveBreak(p))
            {
                aggressive.Add(p.Id);
            }
        }
        if (broken.Count == 0)
        {
            return held;
        }
        // The cell of every observed pawn that stands on a map; a pawn on none has no entry.
        var positions = new Dictionary<string, Cell>(StringComparer.Ordinal);
        if (aggressive.Count > 0)
        {
            var reply = await _config.BreakSource.ReadPawnsAsync(Boundary.Identity(current), ids, ct).ConfigureAwait(false);
            var observed = reply;
            if (observed.Pawns.Count != ids.Count)
            {
                throw new ControlException("breakDispatchHolds: len(observed.Pawns) != len(ids)");
            }
            _ = Boundary.Context(observed.Context, current);
            foreach (var row in observed.Pawns)
            {
                if (BreakDispatch.TryBreakCell(row, out var standing))
                {
                    positions[row.Pawn.Id ?? ""] = standing;
                }
            }
        }
        foreach (var plan in plans)
        {
            var sites = await _player.Journal.DispatchSitesAsync(plan.Spec.Id, ct).ConfigureAwait(false);
            var drafts = new HashSet<ActionId>();
            foreach (var action in plan.Spec.Actions)
            {
                if (action.Kind == ActionKind.Subdue)
                {
                    drafts.Add(action.Subdue!.DraftAction);
                }
            }
            foreach (var action in plan.Spec.Actions)
            {
                var site = sites.TryGetValue(action.Id, out var s) ? s : default;
                if (broken.Contains((PawnId)site.Worker))
                {
                    held.Add(action.Id);
                    continue;
                }
                if (action.Kind == ActionKind.Subdue || drafts.Contains(action.Id))
                {
                    continue;
                }
                foreach (var id in aggressive)
                {
                    if (!positions.TryGetValue(id.Value, out var at))
                    {
                        held.Add(action.Id);
                        break;
                    }
                    // An explicit target within the exclusion zone is unsafe even when its assigned worker starts
                    // outside it. A pawn on no map holds conservatively.
                    var cells = new List<Cell>();
                    if (site.Cell.TryGetValue(out var siteCell))
                    {
                        cells.Add(siteCell);
                    }
                    if (site.Worker.Value.Length != 0)
                    {
                        if (positions.TryGetValue(site.Worker.Value, out var workerAt))
                        {
                            cells.Add(workerAt);
                        }
                        else
                        {
                            held.Add(action.Id);
                        }
                    }
                    if (positions.TryGetValue(site.Target, out var targetAt))
                    {
                        cells.Add(targetAt);
                    }
                    foreach (var cell in cells)
                    {
                        if (BreakResponseRules.InBreakRadius(cell, at))
                        {
                            held.Add(action.Id);
                        }
                    }
                    if (cells.Count == 0)
                    {
                        held.Add(action.Id);
                    }
                }
            }
        }
        return held;
    }
}
