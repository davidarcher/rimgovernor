using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefensePlanner
{
    /// <summary>After the fight: strips the downed raiders of their apparel one at a time, finishes the rest and settles the entities; held while any of that is pending.</summary>
    internal async Task<(RoundsDefenseResult Result, bool Held)> PostFightAsync(IncidentState incident, ControlState state, Tick tick, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        CombatFight? fight = null;
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
            var record = p.Journal.LoadCombatFight(method.Plan, call);
            if (record is { Open: true })
            {
                fight = record;
                fightPlan = method.Plan;
            }
        }
        if (fight is null || fightPlan.IsEmpty)
        {
            return (new RoundsDefenseResult(), false);
        }
        var combat = await _native.ReadCombatAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
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
            var strip = StripState.StripNone;
            if (strips.TryGetValue(id, out var plan))
            {
                strip = StripStateOf(p.Journal, plan, call);
            }
            switch (PolicyApi.PostFightNext(ApparelWorn(combat.Detail.TryGetValue(id.Value, out var worn) ? worn : null), strip))
            {
                case PostFightStep.Wait:
                    return (new RoundsDefenseResult { Verdict = BuildingReasonExistingWork, Plan = fightPlan }, true);
                case PostFightStep.Strip:
                    return (CommitStrip(incident, id, call, epoch), true);
            }
            if (PolicyApi.CaptureWorthy(new CombatPawnState { Luciferium = raider.LuciferiumAddicted }))
            {
                continue; // stripped; the custody capture takes it
            }
            return (await FinishRaiderAsync(state, fightPlan, fight.Memory, combat, raider, arbiter, call, epoch).ConfigureAwait(false), true);
        }
        return await PostFightEntitiesAsync(state, tick, fightPlan, fight.Memory, combat, arbiter, call, epoch).ConfigureAwait(false);
    }

    internal RoundsDefenseResult CommitStrip(IncidentState incident, PawnId raider, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var id = PlanIds.MintPlanId();
        var value = new Strip(raider.Value);
        var action = DomainAction.NewStripAction(new ActionId($"{id}-0"), value);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        p.Journal.CommitFightStrip(incident.Incident.Id, new MethodId(StripMethodPrefix + raider.Value), plan, call);
        return new RoundsDefenseResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }

    /// <summary>Finishes a downed raider: sends the nearest free colonist (or the one already targeting it) to attack.</summary>
    internal async Task<RoundsDefenseResult> FinishRaiderAsync(ControlState state, PlanId fightPlan, CombatMemory memory, BridgeCombat combat, M.CombatPawn raider, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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
        IReadOnlyList<CombatOrder> orders = [new CombatOrder { Pawn = id, Kind = CombatOrderKind.Attack, Target = new PawnId(raider.Id), Reason = CombatOrderReason.Finish }];
        p.Current(call, epoch);
        var tick = new Tick(combat.Context?.Tick ?? 0);
        var label = string.Create(CultureInfo.InvariantCulture, $"{fightPlan}-finish-{raider.Id}-{tick.Value}");
        var batch = CombatBatchPlan(fightPlan, label, drafts, orders);
        var (results, sent) = await SendCombatBatchAsync(state, fightPlan, label, drafts, orders, call).ConfigureAwait(false);
        (results, memory) = RecordDrafts(fightPlan, drafts, results, memory, call);
        var (record, next) = CombatStopRecord(new CombatView { Tick = tick }, sent, results, memory);
        record = record with { Batch = batch.Id };
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

    internal static StripState StripStateOf(StoreJournal journal, PlanId id, CancellationToken call)
    {
        var plan = journal.LoadPlan(id, call);
        if (StorePackage.PlanOpen(plan))
        {
            return StripState.StripOpen;
        }
        foreach (var progress in plan.Progress)
        {
            if (progress.View.Stage == Stage.Completed)
            {
                return StripState.StripPlaced;
            }
        }
        return StripState.StripRefused;
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
