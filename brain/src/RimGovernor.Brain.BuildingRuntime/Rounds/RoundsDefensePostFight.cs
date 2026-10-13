using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefensePlanner
{
    /// <summary>After the fight: strips the downed raiders of their apparel one at a time, finishes the rest and settles the entities; held while any of that is pending.</summary>
    internal (RoundsDefenseResult Result, bool Held) PostFight(CancellationToken call, CancellationToken epoch, IncidentState incident, ControlState state, Tick tick, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        CombatFight fight = new();
        PlanId fightPlan = default;
        var strips = new Dictionary<PawnId, PlanId>();
        foreach (var method in incident.Methods)
        {
            if (method.Method.Value.StartsWith(StripMethodPrefix, StringComparison.Ordinal))
            {
                strips[new PawnId(method.Method.Value[StripMethodPrefix.Length..])] = method.Plan;
                continue;
            }
            if (!method.Method.Value.StartsWith(CombatMethodPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var (record, ok) = p.Journal.LoadCombatFight(method.Plan, call);
            if (ok && record.Open)
            {
                fight = record;
                fightPlan = method.Plan;
            }
        }
        if (fightPlan.IsZero)
        {
            return (new RoundsDefenseResult(), false);
        }
        var combat = _native.ReadCombat(Boundary.Identity(state.Snapshot), call);
        try
        {
            Boundary.Context(combat.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("postFight: combat frame outside the world");
        }
        foreach (var raider in DownedRaiders(combat))
        {
            var id = new PawnId(raider.Id);
            var strip = StripState.None;
            if (strips.TryGetValue(id, out var plan))
            {
                strip = StripStateOf(call, p.Journal, plan);
            }
            switch (PolicyApi.PostFightNext(ApparelWorn(combat.Detail.At(id.Value)), strip))
            {
                case PostFightStep.Wait:
                    return (new RoundsDefenseResult { Verdict = BuildingReasonExistingWork, Plan = fightPlan }, true);
                case PostFightStep.Strip:
                    return (CommitStrip(call, epoch, incident, id), true);
            }
            if (PolicyApi.CaptureWorthy(new CombatPawnState { Luciferium = raider.LuciferiumAddicted }))
            {
                continue; // stripped; the custody capture takes it
            }
            return (FinishRaider(call, epoch, state, fightPlan, fight.Memory, combat, raider, arbiter), true);
        }
        return PostFightEntities(call, epoch, state, tick, fightPlan, fight.Memory, combat, arbiter);
    }

    internal RoundsDefenseResult CommitStrip(CancellationToken call, CancellationToken epoch, IncidentState incident, PawnId raider)
    {
        var p = _reviewer.Player;
        var id = PlanIds.MintPlanId();
        var value = DomainApi.NewStrip(raider.Value);
        var action = DomainAction.NewStripAction(new ActionId($"{id}-0"), value);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        p.Journal.CommitFightStrip(incident.Incident.Id, new MethodId(StripMethodPrefix + raider.Value), plan, call);
        return new RoundsDefenseResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }

    /// <summary>Finishes a downed raider: sends the nearest free colonist (or the one already targeting it) to attack.</summary>
    internal RoundsDefenseResult FinishRaider(CancellationToken call, CancellationToken epoch, ControlState state, PlanId fightPlan, CombatMemory memory, BridgeCombat combat, M.CombatPawn raider, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var waiting = new RoundsDefenseResult { Verdict = BuildingReasonExistingWork, Plan = fightPlan };
        var pawn = Stripper(combat, raider);
        if (pawn is null || (pawn.TargetId == raider.Id && pawn.Job != StripJob && pawn.Drafted))
        {
            return waiting;
        }
        var id = new PawnId(pawn.Id);
        if (!arbiter.TryClaim([id]))
        {
            return waiting;
        }
        IReadOnlyList<PawnId> drafts = pawn.Drafted ? [] : [id];
        IReadOnlyList<CombatOrder> orders = [new CombatOrder { Pawn = id, Kind = OrderKind.Attack, Target = new PawnId(raider.Id), Reason = CombatReason.Finish }];
        p.Current(call, epoch);
        var tick = new Tick(combat.Context.Tick);
        var label = string.Create(CultureInfo.InvariantCulture, $"{fightPlan}-finish-{raider.Id}-{tick.Value}");
        var batch = CombatBatchPlan(fightPlan, label, drafts, orders);
        var (results, sent) = SendCombatBatch(call, state, fightPlan, label, drafts, orders);
        (results, memory) = RecordDrafts(call, fightPlan, drafts, results, memory);
        var (record, next) = CombatStopRecord(new CombatView { Tick = tick }, sent, results, memory);
        record.Batch = batch.Id;
        if (record.Orders.Count > 0)
        {
            p.Journal.RecordCombatStop(fightPlan, record, next, call);
        }
        return new RoundsDefenseResult { Verdict = BuildingReasonCombatOrders, Plan = fightPlan };
    }
}

internal static partial class BuildingRuntimePackage
{
    internal const string StripMethodPrefix = "strip-";
    internal const string StripJob = "Strip";

    /// <summary>The downed humanlike hostile pawns of the combat read, ordinal sorted by id; animals are never stripped or captured here.</summary>
    internal static List<M.CombatPawn> DownedRaiders(BridgeCombat combat)
    {
        var output = new List<M.CombatPawn>();
        foreach (var row in combat.Pawns)
        {
            if (row.Side != M.CombatSide.Hostile || !row.Downed || row.Dead)
            {
                continue;
            }
            if (combat.Detail.TryGetValue(row.Id, out var detail) && detail is not null && detail.HasHumanlike && !detail.Humanlike)
            {
                continue; // an animal has nothing to strip and is never captured here
            }
            output.Add(row);
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return output;
    }

    internal static Fact<bool> ApparelWorn(O.PawnState? row)
    {
        if (row is null || row.Equipment is null)
        {
            return Unknown<bool>();
        }
        return Known(row.Equipment.Apparel.Count > 0);
    }

    internal static StripState StripStateOf(CancellationToken call, StoreJournal journal, PlanId id)
    {
        var plan = journal.LoadPlan(id, call);
        if (StorePackage.PlanOpen(plan))
        {
            return StripState.Open;
        }
        foreach (var progress in plan.Progress)
        {
            if (progress.View.Stage == Stage.Completed)
            {
                return StripState.Placed;
            }
        }
        return StripState.Refused;
    }

    /// <summary>The colonist to finish a raider: the one already targeting it, else the nearest standing one (ties by ordinal id).</summary>
    internal static M.CombatPawn? Stripper(BridgeCombat combat, M.CombatPawn raider)
    {
        M.CombatPawn? best = null;
        long bestDist = -1;
        var at = raider.Cell;
        foreach (var row in combat.Pawns)
        {
            if (row.Side != M.CombatSide.Colonist || row.Downed || row.Dead)
            {
                continue;
            }
            var dist = 1L << 40;
            if (row.TargetId == raider.Id)
            {
                dist = -1;
            }
            else if (row.Cell is { } c && at is not null)
            {
                long dx = c.X - at.X;
                long dz = c.Z - at.Z;
                dist = (dx * dx) + (dz * dz);
            }
            if (best is null || dist < bestDist || (dist == bestDist && string.CompareOrdinal(row.Id, best.Id) < 0))
            {
                best = row;
                bestDist = dist;
            }
        }
        return best;
    }
}
