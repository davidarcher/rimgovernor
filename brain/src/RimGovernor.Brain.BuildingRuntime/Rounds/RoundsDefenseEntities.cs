namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefensePlanner
{
    internal (RoundsDefenseResult Result, bool Done) PostFightEntities(CancellationToken call, CancellationToken epoch, ControlState state, Tick tick, PlanId fightPlan, CombatMemory memory, BridgeCombat combat, StepArbiter arbiter)
    {
        var downed = DownedEntities(combat);
        if (downed.Count == 0)
        {
            return (new RoundsDefenseResult(), false);
        }
        var expected = StepScope(call, _reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, tick))
        {
            throw new ControlException("postFightEntities: census outside the world");
        }
        var read = _reviewer.ObserveOwned(call, _reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        var verdicts = new Dictionary<PawnId, EntityVerdict>();
        foreach (var v in PolicyApi.EntityVerdicts(read.Projection.Facts.Containment))
        {
            verdicts[v.Pawn] = v;
        }
        foreach (var raider in downed)
        {
            var ok = verdicts.TryGetValue(new PawnId(raider.Id), out var v);
            if (!ok || v!.Decision == EntityDecision.Capture)
            {
                continue;
            }
            if (v.Decision == EntityDecision.Refuse)
            {
                DefenseAction(call, "routine-defense", LogLevel.Warn, "refused", "entity_capture_refused", raider.Id, new Dictionary<string, object?> { ["plan"] = fightPlan.Value, ["outcome"] = "left_alive", ["detail"] = v.Reason });
            }
            else if (v.Decision == EntityDecision.Kill)
            {
                DefenseAction(call, "routine-defense", LogLevel.Info, "applied", "entity_kill", raider.Id, new Dictionary<string, object?> { ["plan"] = fightPlan.Value, ["outcome"] = "not_captured", ["detail"] = v.Reason });
                return (FinishRaider(call, epoch, state, fightPlan, memory, combat, raider, arbiter), true);
            }
        }
        return (new RoundsDefenseResult(), false);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The downed hostile pawns the anomaly read marks as entities, ordinal sorted by id.</summary>
    internal static List<M.CombatPawn> DownedEntities(BridgeCombat combat)
    {
        var output = new List<M.CombatPawn>();
        foreach (var row in combat.Pawns)
        {
            if (row.Side != M.CombatSide.Hostile || !row.Downed || row.Dead)
            {
                continue;
            }
            if (!combat.Detail.TryGetValue(row.Id, out var detail) || detail is null)
            {
                continue;
            }
            if (BridgeApi.PawnAnomaly(detail.Anomaly).TryGetValue(out var a) && PositiveFact(a.Entity))
            {
                output.Add(row);
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return output;
    }
}
