namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the mental-break dispatch fence (Go <c>BreakResponseSource</c>).</summary>
public interface IBreakResponseSource
{
    Task<EmergencyObservation> ReadEmergencyAsync(PbCommon.Identity identity, CancellationToken ct);

    Task<PbObservations.ListPawnsReply> ReadCombatPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> pawnIds, CancellationToken ct);
}

/// <summary>Port of break_dispatch.go: releasing a breaking pawn's work and fencing new dispatch near an aggressive break.</summary>
internal static class BreakDispatch
{
    public static Fact<Cell> BreakCell(PbObservations.PawnState row)
    {
        var p = row.Pawn?.Position;
        if (p is null || !p.HasX || !p.HasZ)
        {
            return Unknown<Cell>();
        }
        return Known(new Cell(p.X, p.Z));
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
            if (PolicyFunctions.AggressiveBreak(p))
            {
                aggressive.Add(new PawnId(p.Id.Value));
            }
            if (p.MentalState.IsKnown)
            {
                broken.Add(new PawnId(p.Id.Value));
            }
        }
        var colonistsKnown = f.ColonistsComplete.TryGetValue(out var complete);
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
                if (p.Action.Subdue is { } m && colonistsKnown && complete && !aggressive.Contains((PawnId)m.Target) && !v.Unresolved
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
        if (!census.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete)
        {
            throw new ControlException("breakDispatchHolds: !known || !complete");
        }
        var ids = new List<string>();
        var broken = new HashSet<PawnId>();
        var aggressive = new List<PawnId>();
        foreach (var p in census.Facts.Colonists)
        {
            ids.Add(p.Id.Value);
            if (p.MentalState.IsKnown)
            {
                broken.Add(new PawnId(p.Id.Value));
            }
            if (PolicyFunctions.AggressiveBreak(p))
            {
                aggressive.Add(new PawnId(p.Id.Value));
            }
        }
        if (broken.Count == 0)
        {
            return held;
        }
        var positions = new Dictionary<string, Fact<Cell>>(StringComparer.Ordinal);
        if (aggressive.Count > 0)
        {
            var reply = await _config.BreakSource.ReadCombatPawnsAsync(Boundary.Identity(current), ids, ct).ConfigureAwait(false);
            var observed = reply.Observed ?? throw new ControlException("breakDispatchHolds: observed == nil");
            if (observed.Pawns.Count != ids.Count)
            {
                throw new ControlException("breakDispatchHolds: len(observed.Pawns) != len(ids)");
            }
            _ = Boundary.Context(observed.Context, current);
            foreach (var row in observed.Pawns)
            {
                positions[row.Pawn?.Id ?? ""] = BreakDispatch.BreakCell(row);
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
                    if (!positions.TryGetValue(id.Value, out var aggressorAt) || !aggressorAt.TryGetValue(out var at))
                    {
                        held.Add(action.Id);
                        break;
                    }
                    // An explicit target within the exclusion zone is unsafe even when its assigned worker starts
                    // outside it. Unknown locations hold conservatively.
                    var cells = new List<Fact<Cell>> { site.Cell };
                    if (site.Worker.Value.Length != 0)
                    {
                        if (!positions.TryGetValue(site.Worker.Value, out var workerAt) || !workerAt.IsKnown)
                        {
                            held.Add(action.Id);
                        }
                        cells.Add(workerAt);
                    }
                    if (positions.TryGetValue(site.Target, out var targetAt))
                    {
                        cells.Add(targetAt);
                    }
                    var located = false;
                    foreach (var cell in cells)
                    {
                        if (cell.TryGetValue(out var p))
                        {
                            located = true;
                            if (PolicyFunctions.InBreakRadius(p, at))
                            {
                                held.Add(action.Id);
                            }
                        }
                    }
                    if (!located)
                    {
                        held.Add(action.Id);
                    }
                }
            }
        }
        return held;
    }
}
