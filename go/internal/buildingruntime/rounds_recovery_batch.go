package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

var errNoDefinitions = errors.New("roof rules: the native source serves no definitions")

// recoveryStep plans the recovery queue's roof-first removal batch (#2298) over
// the fresh census rows and the colony's planning window (the whole map), files
// it as a recovery_batch row and returns the clearance step to commit: the roof
// cells to take off first (remove_roof), else the batch to deconstruct together.
// The step is empty with nothing to work, and when the roof rules cannot be read
// (a failed row): no removal is committed on a guess.
func (r *RoundsClearancePlanner) recoveryStep(call context.Context, identity *c.Identity, queue *policy.RecoveryQueue, rows []policy.ClearanceTarget, colony observation.ColonyProjection) policy.GroundStep {
	if queue == nil {
		return policy.GroundStep{}
	}
	targets := policy.RecoveryBatchTargets(*queue, rows)
	if len(targets) == 0 {
		return policy.GroundStep{}
	}
	roofs, err := r.roofRules(call, identity)
	if err != nil {
		telemetry.Decide(call, recoveryBatchFailed(len(targets), err))
		return policy.GroundStep{}
	}
	batch := policy.PlanRecoveryBatch(policy.RecoveryBatchRequest{Grid: colonyRoofGrid(colony), Rules: roofs, Targets: targets})
	telemetry.Decide(call, recoveryBatchDecision(len(targets), batch))
	switch batch.Stage {
	case policy.RecoveryBatchRoof:
		return policy.GroundStep{Phase: policy.GroundWalls, Roof: batch.Roof}
	case policy.RecoveryBatchRemoval:
		byID := make(map[string]policy.ClearanceTarget, len(rows))
		for _, row := range rows {
			byID[row.EntityID] = row
		}
		step := policy.GroundStep{Phase: policy.GroundFurniture}
		for _, id := range batch.Batch {
			step.Targets = append(step.Targets, byID[id])
		}
		return step
	}
	return policy.GroundStep{}
}

// recoveryStepMethod is the method prefix and actions of a recoveryStep: the
// roof-off method, or one deconstruction per batch target.
func recoveryStepMethod(id domain.PlanID, step policy.GroundStep) (string, []domain.Action, error) {
	if len(step.Targets) == 0 {
		return groundStepMethod(id, step)
	}
	actions, err := groundActions(id, step, nil)
	return batchPrefix("deconstruct", step.Targets[0].EntityID, len(step.Targets)), actions, err
}

func (r *RoundsClearancePlanner) roofRules(call context.Context, identity *c.Identity) (policy.RoofRules, error) {
	definitions, ok := r.native.(observation.DefinitionSource)
	if !ok {
		return nil, errNoDefinitions
	}
	catalog, err := definitions.DefinitionCatalog(call, identity)
	if err != nil {
		return nil, err
	}
	return catalog.RoofRules()
}

// colonyRoofGrid is the joint roof check's input over the projection's
// planning window: the mirror rows by cell, the map bounds and the roof
// support radius. The window covers the whole map, so a cell it does not list
// is fogged and reads as unknown geometry.
func colonyRoofGrid(colony observation.ColonyProjection) policy.RoofSupportGrid {
	cells := make(map[domain.Cell]policy.SiteCell, len(colony.Cells))
	for _, cell := range colony.Cells {
		cells[cell.Cell] = cell
	}
	return policy.RoofSupportGrid{
		Cell:   func(cell domain.Cell) (policy.SiteCell, bool) { row, ok := cells[cell]; return row, ok },
		Bounds: policy.Rectangle{Width: colony.Bounds.Width, Height: colony.Bounds.Height},
		Radius: colony.RoofSupport,
	}
}

// recoveryBatchDecision is the recovery_batch row: reason the stage, attrs the
// queue's removal count, the roof cell count, the batch ids, each held target
// with its hold word and the first joint-check blocker.
func recoveryBatchDecision(targets int, batch policy.RecoveryBatch) telemetry.Decision {
	held := make([]map[string]any, len(batch.Held))
	for i, h := range batch.Held {
		held[i] = map[string]any{"id": h.ID, "reason": h.Reason}
	}
	verdict := "planned"
	if batch.Stage == policy.RecoveryBatchNone {
		verdict = "held"
	}
	return telemetry.Decision{Kind: "recovery_batch", Component: "clock-scheduler", Verdict: verdict, Reason: string(batch.Stage), Target: "clearance",
		Attrs: map[string]any{"targets": targets, "roof_cells": len(batch.Roof), "batch": batch.Batch, "held": held, "blocker": batch.Blocker}}
}

func recoveryBatchFailed(targets int, err error) telemetry.Decision {
	return telemetry.Decision{Kind: "recovery_batch", Component: "clock-scheduler", Verdict: "failed", Reason: "roof_rules", Target: "clearance",
		Attrs: map[string]any{"targets": targets, "error": err}}
}
