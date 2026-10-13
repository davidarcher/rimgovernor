namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsClearancePlanner
{
    internal GroundStep RecoveryStep(CancellationToken call, C.Identity identity, RecoveryQueue? queue, IReadOnlyList<ClearanceTarget> rows, ColonyProjection colony)
    {
        if (queue is null)
        {
            return default;
        }

        var targets = PolicyFunctions.RecoveryBatchTargets(queue, rows);
        if (targets.Count == 0)
        {
            return default;
        }

        RoofRules roofs;
        try
        {
            roofs = RoofRulesOf(call, identity);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Tel.Decide(call, RecoveryBatchFailed(targets.Count, e));
            return default;
        }

        var batch = PolicyFunctions.PlanRecoveryBatch(new RecoveryBatchRequest { Grid = ColonyRoofGrid(colony), Rules = roofs, Targets = targets });
        Tel.Decide(call, RecoveryBatchDecision(targets.Count, batch));
        switch (batch.Stage)
        {
            case RecoveryBatchStage.Roof:
                return new GroundStep { Phase = GroundPhase.Walls, Roof = batch.Roof };
            case RecoveryBatchStage.Removal:
            {
                var byId = rows.ToDictionary(r => r.EntityId);
                var step = new GroundStep { Phase = GroundPhase.Furniture, Targets = batch.Batch.Select(id => byId[id]).ToList() };
                return step;
            }
        }

        return default;
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

    internal RoofRules RoofRulesOf(CancellationToken call, C.Identity identity)
    {
        if (Native is not IDefinitionSource definitions)
        {
            throw new InvalidOperationException("roof rules: the native source serves no definitions");
        }

        return definitions.DefinitionCatalog(call, identity).RoofRules();
    }

    internal static RoofSupportGrid ColonyRoofGrid(ColonyProjection colony)
    {
        var cells = colony.Cells.ToDictionary(c => c.Cell);
        return new RoofSupportGrid
        {
            Cell = cell => cells.TryGetValue(cell, out var row) ? (row, true) : (default, false),
            Bounds = new Rectangle(0, 0, colony.Bounds.Width, colony.Bounds.Height),
            Radius = colony.RoofSupport,
        };
    }

    internal static Decision RecoveryBatchDecision(int targets, RecoveryBatch batch)
    {
        var held = batch.Held.Select(h => new Dictionary<string, object?> { ["id"] = h.Id, ["reason"] = h.Reason }).ToList();
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
