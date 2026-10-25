using System.Globalization;

namespace RimGovernor.Brain.Runtime;

public sealed partial class DefensePlanner
{
    /// <summary>After the fight: strips the downed raiders of their apparel one at a time, finishes the rest and settles the entities; held while any of that is pending.</summary>
    internal async Task<(DefenseResult Result, bool Held)> PostFightAsync(NeedOwner owner, ControlState state, Tick tick, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        CombatFight? fight = null;
        PlanId fightPlan = default;
        foreach (var method in owner.Methods)
        {
            if (!IsFightMethod(method.Method))
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
            return (new DefenseResult(), false);
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
            var id = raider.Id;
            var strip = StripState.StripNone;
            // The raider's latest strip, settled ones included: a stripped raider is not stripped again.
            if (p.Journal.OwnerMethodPlan(owner, StripMethod(id), call) is { } plan)
            {
                strip = StripStateOf(p.Journal, plan, call);
            }
            switch (CombatThreatRules.PostFightNext(ApparelOf(combat.Detail, id), strip))
            {
                case PostFightStep.Wait:
                    return (new DefenseResult { Verdict = BuildingReasonExistingWork, Plan = fightPlan }, true);
                case PostFightStep.Strip:
                    return (CommitStrip(state.Snapshot, id, call, epoch), true);
            }
            if (CombatThreatRules.CaptureWorthy(raider.LuciferiumAddicted))
            {
                continue; // stripped; the custody capture takes it
            }
            return (await FinishRaiderAsync(state, fightPlan, fight.Memory, combat, raider, arbiter, call, epoch).ConfigureAwait(false), true);
        }
        return await PostFightEntitiesAsync(state, tick, fightPlan, fight.Memory, combat, arbiter, call, epoch).ConfigureAwait(false);
    }

    /// <summary>The strip Method of a downed raider, named by its target: <c>strip-&lt;raider&gt;</c>.</summary>
    internal static MethodId StripMethod(PawnId raider) => new(StripMethodPrefix + raider.Value);

    internal DefenseResult CommitStrip(GenerationSnapshot snapshot, PawnId raider, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var id = PlanIds.MintPlanId();
        var value = new Strip(raider.Value);
        var action = DomainAction.NewStripAction(new ActionId($"{id}-0"), value);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        p.Journal.CommitFightStrip(snapshot, StripMethod(raider), plan, call);
        return new DefenseResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }

    /// <summary>Finishes a downed raider: sends the nearest free colonist (or the one already targeting it) to attack.</summary>
    internal async Task<DefenseResult> FinishRaiderAsync(ControlState state, PlanId fightPlan, CombatMemory memory, BridgeCombat combat, RimGovernor.Brain.GameFacts.Mirror.CombatPawn raider, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var waiting = new DefenseResult { Verdict = BuildingReasonExistingWork, Plan = fightPlan };
        var pawn = Stripper(combat, raider);
        if (pawn is null || ((pawn.TargetId ?? "") == raider.Id.Value && (pawn.Job ?? "") != StripJob && (pawn.Draft is not null)))
        {
            return waiting;
        }
        var id = pawn.Id;
        if (!arbiter.TryClaim([id]))
        {
            return waiting;
        }
        IReadOnlyList<PawnId> drafts = (pawn.Draft is not null) ? [] : [id];
        IReadOnlyList<CombatOrder> orders = [new CombatOrder { Pawn = id, Kind = CombatOrderKind.Attack, Target = raider.Id, Reason = CombatOrderReason.Finish }];
        p.Current(call, epoch);
        var tick = new Tick(combat.Context?.Tick ?? 0);
        var label = string.Create(CultureInfo.InvariantCulture, $"{fightPlan}-finish-{raider.Id.Value}-{tick.Value}");
        var batch = CombatBatchPlan(fightPlan, label, drafts, orders);
        var (results, sent) = await SendCombatBatchAsync(state, fightPlan, label, drafts, orders, call).ConfigureAwait(false);
        (results, memory) = RecordDrafts(fightPlan, drafts, results, memory, call);
        var next = CombatStopMemory([], sent, results, memory);
        if (sent.Count > 0)
        {
            p.Journal.RecordCombatStop(fightPlan, batch.Id, sent.Count, next, call);
        }
        return new DefenseResult { Verdict = BuildingReasonCombatOrders, Plan = fightPlan };
    }
}

partial class DefensePlanner
{
    internal const string StripMethodPrefix = "strip-";
    internal const string StripJob = "Strip";

    /// <summary>The downed humanlike hostile pawns of the combat read, ordinal sorted by id; animals are never stripped or captured here.</summary>
    internal static List<RimGovernor.Brain.GameFacts.Mirror.CombatPawn> DownedRaiders(BridgeCombat combat)
    {
        var output = new List<RimGovernor.Brain.GameFacts.Mirror.CombatPawn>();
        foreach (var row in combat.Pawns)
        {
            if (row.Side != RimGovernor.Brain.GameFacts.Mirror.CombatSide.Hostile || !row.Downed || row.Dead)
            {
                continue;
            }
            if (combat.Detail.TryGetValue(row.Id.Value, out var detail) && detail is not null && !detail.Humanlike)
            {
                continue; // an animal has nothing to strip and is never captured here
            }
            output.Add(row);
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Id.Value, b.Id.Value));
        return output;
    }

    /// <summary>What the frame's pawn table shows the raider wearing.</summary>
    internal static RaiderApparel ApparelOf(Pawns detail, PawnId raider)
    {
        if (!detail.TryGetValue(raider.Value, out var row))
        {
            return RaiderApparel.NotInFrame;
        }
        return row.Equipment.Apparel.Count > 0 ? RaiderApparel.Wearing : RaiderApparel.Bare;
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
    internal static RimGovernor.Brain.GameFacts.Mirror.CombatPawn? Stripper(BridgeCombat combat, RimGovernor.Brain.GameFacts.Mirror.CombatPawn raider)
    {
        RimGovernor.Brain.GameFacts.Mirror.CombatPawn? best = null;
        long bestDist = -1;
        var at = raider.Cell;
        foreach (var row in combat.Pawns)
        {
            if (row.Side != RimGovernor.Brain.GameFacts.Mirror.CombatSide.Colonist || row.Downed || row.Dead)
            {
                continue;
            }
            var dist = 1L << 40;
            if ((row.TargetId ?? "") == raider.Id.Value)
            {
                dist = -1;
            }
            else if (row.Cell is { } c)
            {
                long dx = c.X - at.X;
                long dz = c.Z - at.Z;
                dist = (dx * dx) + (dz * dz);
            }
            if (best is null || dist < bestDist || (dist == bestDist && string.CompareOrdinal(row.Id.Value, best.Id.Value) < 0))
            {
                best = row;
                bestDist = dist;
            }
        }
        return best;
    }
}
