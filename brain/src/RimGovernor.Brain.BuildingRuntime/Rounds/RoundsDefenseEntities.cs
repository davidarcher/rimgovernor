using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefensePlanner
{
    internal async Task<(RoundsDefenseResult Result, bool Done)> PostFightEntitiesAsync(ControlState state, Tick tick, PlanId fightPlan, CombatMemory memory, BridgeCombat combat, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var downed = DownedEntities(combat);
        if (downed.Count == 0)
        {
            return (new RoundsDefenseResult(), false);
        }
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, tick))
        {
            throw new ControlException("postFightEntities: census outside the world");
        }
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var verdicts = new Dictionary<Domain.PawnId, EntityVerdict>();
        foreach (var v in EntityCaptureRules.EntityVerdicts(read.Projection.Facts.Containment))
        {
            verdicts[v.Pawn] = v;
        }
        foreach (var raider in downed)
        {
            var ok = verdicts.TryGetValue(raider.Id, out var v);
            if (!ok || v!.Decision == EntityDecision.Capture)
            {
                continue;
            }
            if (v.Decision == EntityDecision.Refuse)
            {
                DefensePipeline.DefenseAction("routine-defense", TelemetryLevel.Warn, "refused", "entity_capture_refused", raider.Id.Value, new Dictionary<string, object?> { ["plan"] = fightPlan.Value, ["outcome"] = "left_alive", ["detail"] = v.Reason }, call);
            }
            else if (v.Decision == EntityDecision.Kill)
            {
                DefensePipeline.DefenseAction("routine-defense", TelemetryLevel.Info, "applied", "entity_kill", raider.Id.Value, new Dictionary<string, object?> { ["plan"] = fightPlan.Value, ["outcome"] = "not_captured", ["detail"] = v.Reason }, call);
                return (await FinishRaiderAsync(state, fightPlan, memory, combat, raider, arbiter, call, epoch).ConfigureAwait(false), true);
            }
        }
        return (new RoundsDefenseResult(), false);
    }
}

partial class RoundsDefensePlanner
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
            if (!combat.Detail.TryGetValue(row.Id.Value, out var detail) || detail is null)
            {
                continue;
            }
            if (detail.Anomaly is RimGovernor.Contracts.Wire.Observations.PawnAnomalyState.Active { Facts.Entity: true })
            {
                output.Add(row);
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Id.Value, b.Id.Value));
        return output;
    }
}
