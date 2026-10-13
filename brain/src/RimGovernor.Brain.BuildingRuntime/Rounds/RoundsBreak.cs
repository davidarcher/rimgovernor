using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefensePlanner
{
    internal const string SubdueMethodPrefix = "combat-subdue-";

    /// <summary>
    /// Settles an open subdue fight of the incident: keeps it while the subdued colonist is still breaking, otherwise
    /// cancels the open work, stops the owned drafts and closes the fight once the stop is confirmed.
    /// </summary>
    internal (RoundsDefenseResult Result, bool Handled) ReconcileSubdueFight(CancellationToken call, CancellationToken epoch, IncidentState incident, ControlState state)
    {
        var p = _reviewer.Player;
        foreach (var method in incident.Methods)
        {
            if (!method.Method.Value.StartsWith(SubdueMethodPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var (fight, found) = p.Journal.LoadCombatFight(method.Plan, call);
            if (!found || !fight.Open)
            {
                continue;
            }
            var plan = p.Journal.LoadPlan(method.Plan, call);
            var combat = _native.ReadCombat(BoundaryApi.Identity(state.Snapshot), call);
            try
            {
                BoundaryApi.Context(combat.Context, state.Snapshot);
            }
            catch (ControlException)
            {
                throw new ControlException("subdue needs a complete current census");
            }
            if (combat.Emergency.Facts.ColonistsComplete != Known(true))
            {
                throw new ControlException("subdue needs a complete current census");
            }
            var participants = new HashSet<PawnId>();
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
                            active = active || (new PawnId(pawn.Id) == subdue.Target && PolicyApi.AggressiveBreak(pawn));
                        }
                    }
                }
            }
            var stop = new StopEvent();
            foreach (var ev in combat.Events)
            {
                var stopKind = ev.Stop;
                if (ev.At.Tick > (long)fight.Memory.Tick && participants.Contains(new PawnId(ev.ThingId))
                    && (stopKind == K.CombatEvent.SeriousInjury || stopKind == K.CombatEvent.Downed))
                {
                    stop = new StopEvent { Kind = new CombatStopKind(stopKind == K.CombatEvent.SeriousInjury ? "serious_injury" : "downed"), Pawn = new PawnId(ev.ThingId) };
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
                    orders.Add(new CombatOrder { Pawn = draft.Pawn, Kind = PolicyApi.OrderStop });
                }
            }
            var batchLabel = $"{method.Plan}-subdue-stop-{combat.Context.Tick.ToString(CultureInfo.InvariantCulture)}";
            var batch = CombatBatchPlan(method.Plan, batchLabel, null, orders);
            var (results, _) = SendCombatBatch(call, state, method.Plan, batchLabel, null, orders);
            var confirmed = results is not null;
            if (results is null)
            {
                var fresh = _native.ReadCombat(BoundaryApi.Identity(state.Snapshot), call);
                BoundaryApi.Context(fresh.Context, state.Snapshot);
                confirmed = true;
                foreach (var order in orders)
                {
                    var ok = fresh.Detail.TryGetValue(order.Pawn.Value, out var row);
                    confirmed = confirmed && ok && row is not null && row.Job is not null && row.Job.HasPlayerForced && !row.Job.PlayerForced
                        && row.Job.HasQueuedJobs && row.Job.QueuedJobs == 0 && row.Job.Issues.Count == 0;
                }
                if (confirmed)
                {
                    var saved = p.Journal.LoadPlan(batch.Id, call);
                    var v = saved.Progress[0].View;
                    if (v.Unresolved)
                    {
                        var scope = state.Snapshot with { Plan = batch.Id, Revision = batch.Revision };
                        p.Journal.Observe(batch.Id, new Observation
                        {
                            Action = v.Action,
                            Attempt = v.Attempt,
                            Snapshot = scope,
                            Tick = new Tick(fresh.Context.Tick),
                            Effect = Effect.Completed,
                            Causality = Causality.AfterDispatch,
                        }, scope, call);
                        p.Journal.RetireCombatBatch(batch.Id, call);
                    }
                }
            }
            var record = new CombatStopRecord { Tick = new Tick(combat.Context.Tick), Stop = stop, Batch = batch.Id };
            for (var i = 0; i < orders.Count; i++)
            {
                var entry = new CombatOrderRecord { CombatOrder = orders[i], Uncertain = results is null };
                if (results is not null)
                {
                    entry.Applied = results[i].Applied;
                    entry.Refusal = results[i].Refusal;
                    confirmed = confirmed && (entry.Applied || entry.Refusal == BridgeApi.CombatRefusalNotDrafted || entry.Refusal == BridgeApi.CombatRefusalNotFound);
                }
                record.Orders.Add(entry);
            }
            if (record.Orders.Count > 0)
            {
                p.Journal.RecordCombatStop(method.Plan, record, fight.Memory, call);
            }
            if (confirmed && !unresolved)
            {
                p.Journal.CloseCombatFight(method.Plan, call);
            }
            return (result, true);
        }
        return (new RoundsDefenseResult(), false);
    }

    /// <summary>The melee fact of a pawn row: whether its primary weapon is a melee weapon.</summary>
    internal static Fact<bool> BreakMelee(O.PawnState row, Armament arms)
    {
        var (weapon, known) = arms.Primary(row.Equipment);
        if (!known)
        {
            return Unknown<bool>();
        }
        return Known(weapon.Melee);
    }

    internal static bool HasAggressiveBreak(EmergencyFacts f)
    {
        foreach (var p in f.Colonists)
        {
            if (PolicyApi.AggressiveBreak(p))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Admits a subdue plan for the first aggressive breaking colonist a squad can answer.</summary>
    internal RoundsDefenseResult PlanBreak(CancellationToken call, CancellationToken epoch, IncidentState incident, ControlState state, DateTimeOffset started, StepArbiter arbiter, Tick tick, EmergencyFacts f, IReadOnlyDictionary<string, O.PawnState> rows)
    {
        var interrupted = new HashSet<PawnId>();
        foreach (var method in incident.Methods)
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
            if (PolicyApi.AggressiveBreak(p0) && !interrupted.Contains(p0.Id))
            {
                targets.Add(p0);
            }
        }
        if (targets.Count == 0)
        {
            return new RoundsDefenseResult();
        }
        targets.Sort((a, b) => string.CompareOrdinal(a.Id.Value, b.Id.Value));
        var needed = PlannedDrafts(call, _reviewer.Player.Journal);
        var responders = new List<BreakResponder>();
        var arms = ReadArmament(call, _native, BoundaryApi.Identity(state.Snapshot));
        foreach (var p0 in f.Colonists)
        {
            var row = rows[p0.Id.Value];
            var ranged = RangedWeaponEquipped(row.Equipment, arms);
            var d = SquadDefenderFacts(row, needed, ranged);
            d.MeleeEquipped = BreakMelee(row, arms);
            responders.Add(new BreakResponder { SquadDefenderFacts = d, Cell = BreakCell(row) });
        }
        IReadOnlyList<PawnId> chosen = [];
        PawnId victim = default;
        foreach (var target in targets)
        {
            chosen = PolicyApi.SelectBreakSquad(target, BreakCell(rows[target.Id.Value]), responders);
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
        var claim = chosen.ToList();
        claim.Add(victim);
        if (!arbiter.TryClaim(claim))
        {
            return new RoundsDefenseResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "break_pawn_claim") };
        }
        var h = new StringBuilder();
        h.Append(victim.Value).Append('/').Append('[').Append(string.Join(' ', chosen.Select(c => c.Value))).Append(']');
        var methodId = DefenseMethodId("combat-subdue", incident.Methods.Count, h);
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
        pl.Journal.CommitCombatFight(incident.Incident.Id, methodId, spec, NewFightMemory(tick), PlayerWorld(state.Snapshot), chosen, call);
        return new RoundsDefenseResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
