using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefensePlanner
{
    internal const string SubdueMethodPrefix = "subdue-";

    /// <summary>
    /// Settles an open subdue fight of the combat Need: keeps it while the subdued colonist is still breaking, otherwise
    /// cancels the open work, stops the owned drafts and closes the fight once the stop is confirmed.
    /// </summary>
    internal async Task<(RoundsDefenseResult Result, bool Handled)> ReconcileSubdueFightAsync(NeedOwner owner, ControlState state, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        foreach (var method in owner.Methods)
        {
            if (!method.Method.Value.StartsWith(SubdueMethodPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var fight = p.Journal.LoadCombatFight(method.Plan, call);
            if (fight is null || !fight.Open)
            {
                continue;
            }
            var plan = p.Journal.LoadPlan(method.Plan, call);
            var combat = await _native.ReadCombatAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
            if (combat.Context is not { } combatContext || !RoundsArmoryPlanner.ContextMatches(combatContext, state.Snapshot))
            {
                throw new ControlException("subdue needs a complete current census");
            }
            if (!combat.Emergency.Facts.AllSeen)
            {
                throw new ControlException("subdue needs a complete current census");
            }
            var participants = new HashSet<Domain.PawnId>();
            var active = false;
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.Subdue is { } subdue)
                {
                    participants.Add(subdue.Pawn);
                    participants.Add(subdue.Target);
                    if (progress.View.Stage != Stage.Cancelled && progress.View.Stage != Stage.Unsuccessful)
                    {
                        foreach (var pawn in combat.Emergency.Facts.Colonists)
                        {
                            active = active || (pawn.Id == subdue.Target && BreakResponseRules.AggressiveBreak(pawn));
                        }
                    }
                }
            }
            foreach (var ev in combat.Events)
            {
                if (ev.At.Tick > fight.Memory.Tick.Value && participants.Contains(new Domain.PawnId((ev.ThingId ?? "")))
                    && ev.Stop is { Kind: K.CombatEvent.SeriousInjury or K.CombatEvent.Downed })
                {
                    active = false;
                }
            }
            var result = new RoundsDefenseResult { Verdict = BuildingReasonExistingWork, Plan = method.Plan };
            if (active)
            {
                return (result, true);
            }
            try
            {
                p.Current(call, epoch);
            }
            catch (ControlException ex)
            {
                throw new ControlException($"subdue cleanup authority changed: {ex.Message}");
            }
            if (p.Session.State() != state)
            {
                throw new ControlException("subdue cleanup authority changed: <nil>");
            }
            var unresolved = false;
            foreach (var progress in plan.Progress)
            {
                var v = progress.View;
                unresolved = unresolved || v.Unresolved;
                if (v.Stage == Stage.Cancelled || v.Stage == Stage.Unsuccessful || (v.Stage == Stage.Completed && progress.Action.Kind != ActionKind.Subdue))
                {
                    continue;
                }
                p.Journal.Cancel(method.Plan, v.Action, call);
            }
            var orders = new List<CombatOrder>();
            foreach (var action in plan.Spec.Actions)
            {
                if (action.OwnedDraft is { } draft && fight.Roster.Contains(draft.Pawn))
                {
                    orders.Add(new CombatOrder { Pawn = draft.Pawn, Kind = CombatOrderKind.Stop });
                }
            }
            var batchLabel = $"{method.Plan}-subdue-stop-{combatContext.Tick.ToString(CultureInfo.InvariantCulture)}";
            var batch = CombatBatchPlan(method.Plan, batchLabel, [], orders);
            var (results, _) = await SendCombatBatchAsync(state, method.Plan, batchLabel, [], orders, call).ConfigureAwait(false);
            var confirmed = results is not null;
            if (results is null)
            {
                var fresh = await _native.ReadCombatAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
                var freshContext = fresh.Context ?? throw new AuthorityException();
                Boundary.Context(freshContext, state.Snapshot);
                confirmed = true;
                foreach (var order in orders)
                {
                    var ok = fresh.Detail.TryGetValue(order.Pawn.Value, out var row);
                    confirmed = confirmed && ok && row is not null && !row.Job.PlayerForced
                        && row.Job.QueuedJobs == 0;
                }
                if (confirmed)
                {
                    var saved = p.Journal.LoadPlan(batch.Id, call);
                    var v = saved.Progress[0].View;
                    if (v.Unresolved)
                    {
                        var scope = state.Snapshot with { Plan = batch.Id, Revision = batch.Revision };
                        p.Journal.Observe(batch.Id, new Domain.Observation(
                            v.Action,
                            v.Attempt,
                            scope,
                            new Tick(freshContext.Tick),
                            Effect.Completed,
                            ObservationCausality.AfterDispatch), scope, call);
                        p.Journal.RetireCombatBatch(batch.Id, call);
                    }
                }
            }
            if (results is not null)
            {
                for (var i = 0; i < orders.Count; i++)
                {
                    var refusal = results[i].Refusal;
                    confirmed = confirmed && (results[i].Applied || refusal == CombatConstants.CombatRefusalNotDrafted || refusal == CombatConstants.CombatRefusalNotFound);
                }
            }
            if (orders.Count > 0)
            {
                p.Journal.RecordCombatStop(method.Plan, batch.Id, orders.Count, fight.Memory, call);
            }
            if (confirmed && !unresolved)
            {
                p.Journal.CloseCombatFight(method.Plan, call);
            }
            return (result, true);
        }
        return (new RoundsDefenseResult(), false);
    }

    internal static bool HasAggressiveBreak(EmergencyFacts f)
    {
        foreach (var p in f.Colonists)
        {
            if (BreakResponseRules.AggressiveBreak(p))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Admits a subdue plan for the first aggressive breaking colonist a squad can answer.</summary>
    internal async Task<RoundsDefenseResult> PlanBreakAsync(NeedOwner owner, ControlState state, DateTimeOffset started, StepArbiter arbiter, Tick tick, EmergencyFacts f, IReadOnlyDictionary<string, O.PawnState> rows, CancellationToken call, CancellationToken epoch)
    {
        var interrupted = new HashSet<PawnId>();
        foreach (var method in owner.Methods)
        {
            if (!method.Method.Value.StartsWith(SubdueMethodPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var plan0 = _reviewer.Player.Journal.LoadPlan(method.Plan, call);
            foreach (var progress in plan0.Progress)
            {
                if (progress.Action.Subdue is { } subdue && (progress.View.Stage == Stage.Cancelled || progress.View.Stage == Stage.Unsuccessful))
                {
                    interrupted.Add(subdue.Target);
                }
            }
        }
        var targets = new List<EmergencyPawn>();
        foreach (var p0 in f.Colonists)
        {
            if (BreakResponseRules.AggressiveBreak(p0) && !interrupted.Contains(p0.Id))
            {
                targets.Add(p0);
            }
        }
        if (targets.Count == 0)
        {
            return new RoundsDefenseResult();
        }
        targets.Sort((a, b) => string.CompareOrdinal(a.Id.Value, b.Id.Value));
        var needed = await DraftNeeds.PlannedDraftsAsync(_reviewer.Player.Journal, call).ConfigureAwait(false);
        var responders = new List<BreakResponder>();
        var arms = await Armament.ReadArmamentAsync(_native, Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        foreach (var p0 in f.Colonists)
        {
            var row = rows[p0.Id.Value];
            var d = Armament.SquadDefenderFacts(row, needed.Select(n => n).ToHashSet(), arms, new DefenderArmor.Unmirrored());
            responders.Add(new BreakResponder { SquadDefenderFacts = d, Cell = BreakDispatch.BreakCell(row) });
        }
        var preferences = _reviewer.PreferencesOf(state.Snapshot, call);
        var heldBack = PreferenceRules.HeldBackFromDraft(preferences, responders.Select(r => r.SquadDefenderFacts));
        responders = [.. responders.Where(r => !heldBack.Contains(r.ID))];
        List<PawnId> chosen = [];
        PawnId victim = default;
        foreach (var target in targets)
        {
            chosen = BreakResponseRules.SelectBreakSquad(target, BreakDispatch.BreakCell(rows[target.Id.Value]), responders);
            if (chosen.Count > 0)
            {
                victim = target.Id;
                break;
            }
        }
        if (chosen.Count == 0)
        {
            return new RoundsDefenseResult { Verdict = BuildingReasonNoSquad };
        }
        var protectedDrafted = chosen.Where(c => preferences.ProtectsPawn(c.Value)).Select(c => c.Value).ToList();
        if (protectedDrafted.Count > 0)
        {
            DefensePipeline.DefenseAction("routine-defense", TelemetryLevel.Info, "admitted", "protected_pawn_drafted_no_other_fighter", SubdueMethodPrefix + victim.Value, new Dictionary<string, object?> { ["pawns"] = string.Join(",", protectedDrafted) }, call);
        }
        var claim = chosen.ToList();
        claim.Add(victim);
        if (!arbiter.TryClaim(claim))
        {
            return new RoundsDefenseResult { Verdict = WaitFor(Cause.MethodUsed, "break_pawn_claim") };
        }
        var methodId = new MethodId(SubdueMethodPrefix + victim.Value);
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        for (var i = 0; i < chosen.Count; i++)
        {
            var pawn = chosen[i];
            var draftId = new ActionId($"{id}-d{i}");
            var da = DomainAction.NewOwnedDraftAction(draftId, new OwnedDraft(pawn));
            var ma = DomainAction.NewSubdueAction(new ActionId($"{id}-s{i}"), new Subdue(pawn, victim, draftId));
            actions.Add(da);
            actions.Add(ma);
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        var pl = _reviewer.Player;
        pl.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (pl.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("planBreak: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        pl.Journal.CommitCombatFight(state.Snapshot, methodId, spec, NewFightMemory(tick), [.. chosen.Select(c => (Domain.PawnId)c)], call);
        return new RoundsDefenseResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
