namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsClearancePlanner
{
    internal async Task<GroundStep> RecoveryStepAsync(C.Identity identity, RecoveryQueue? queue, IReadOnlyList<ClearanceTarget> rows, ColonyProjection colony, CancellationToken call)
    {
        if (queue is null)
        {
            return new GroundStep();
        }

        var targets = PolicyFunctions.RecoveryBatchTargets(queue, rows);
        if (targets.Count == 0)
        {
            return new GroundStep();
        }

        RoofRules roofs;
        try
        {
            roofs = await RoofRulesOfAsync(identity, call).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Tel.Decide(RecoveryBatchFailed(targets.Count, e));
            return new GroundStep();
        }

        var batch = PolicyFunctions.PlanRecoveryBatch(new RecoveryBatchRequest { Grid = ColonyRoofGrid(colony), Rules = roofs, Targets = targets });
        Tel.Decide(RecoveryBatchDecision(targets.Count, batch));
        switch (batch.Stage)
        {
            case RecoveryBatchStage.Roof:
                return new GroundStep { Phase = GroundPhase.Walls, Roof = batch.Roof };
            case RecoveryBatchStage.Removal:
            {
                var byId = rows.ToDictionary(r => r.EntityId);
                var step = new GroundStep { Phase = GroundPhase.Furniture, Targets = [.. batch.Batch.Select(id => byId[id])] };
                return step;
            }
        }

        return new GroundStep();
    }

    internal static (string Method, IReadOnlyList<DomainAction> Actions) RecoveryStepMethod(PlanId id, GroundStep step)
    {
        if (step.Targets.Count == 0)
        {
            return BuildingRuntimePackage.GroundStepMethod(id, step);
        }

        var actions = BuildingRuntimePackage.GroundActions(id, step);
        return (BuildingRuntimePackage.BatchPrefix("deconstruct", step.Targets[0].EntityId, step.Targets.Count), actions);
    }

    internal async Task<RoofRules> RoofRulesOfAsync(C.Identity identity, CancellationToken call)
    {
        if (_native is not IDefinitionSource definitions)
        {
            throw new InvalidOperationException("roof rules: the native source serves no definitions");
        }

        var catalog = await definitions.DefinitionCatalogAsync(identity, call).ConfigureAwait(false);
        return (catalog ?? throw new InvalidOperationException("roof rules: no definition catalog")).RoofRules();
    }

    internal static RoofSupportGrid ColonyRoofGrid(ColonyProjection colony)
    {
        var cells = colony.Cells.ToDictionary(c => c.Cell);
        return new RoofSupportGrid
        {
            Cell = cell => cells.TryGetValue(cell, out var row) ? row : null,
            Bounds = new Rectangle(0, 0, colony.Bounds.Width, colony.Bounds.Height),
            Radius = colony.RoofSupport,
        };
    }

    internal static Decision RecoveryBatchDecision(int targets, RecoveryBatch batch)
    {
        var held = batch.Held.Select(h => new Dictionary<string, object?> { ["id"] = h.ID,
 ["reason"] = h.Reason }).ToList();
        var verdict = batch.Stage == RecoveryBatchStage.None ? "held" : "planned";
        return new Decision
        {
            Kind = "recovery_batch",
            Component = "clock-scheduler",
            Verdict = verdict,
            Reason = batch.Stage.ToString(),
            Target = "clearance",
            Attrs = new Dictionary<string, object?> { ["targets"] = targets, ["roof_cells"] = batch.Roof.Count, ["batch"] = batch.Batch, ["held"] = held, ["blocker"] = batch.Blocker },
        };
    }

    internal static Decision RecoveryBatchFailed(int targets, Exception error) => new()
    {
        Kind = "recovery_batch",
        Component = "clock-scheduler",
        Verdict = "failed",
        Reason = "roof_rules",
        Target = "clearance",
        Attrs = new Dictionary<string, object?> { ["targets"] = targets, ["error"] = error.Message },
    };
}
