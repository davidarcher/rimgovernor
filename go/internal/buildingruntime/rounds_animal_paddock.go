package buildingruntime

import (
	"context"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The paddock: the yard inside the defensive wall is the
// pen. One PenMarker on a free yard cell claims it once the core ring stands
// and the killbox lane is fenced; until then roamers stay barn-bound. A ring
// that can never close (ReservePerimeterGap) never gets a marker.

// maxPaddockCandidates bounds the cells tried when the native refuses a
// marker placement.
const maxPaddockCandidates = 8

// paddockClosed reports the yard enclosed: the plan's ring has no cell nothing
// can close, every core ring section stands, and, when the plan fences the
// killbox lane, so do its fence sections.
func paddockClosed(record store.DefenseLayoutRecord, plan policy.LayoutPlan) bool {
	fenced := false
	for _, r := range plan.Reservations {
		switch r.Kind {
		case policy.ReservePerimeterGap:
			return false
		case policy.ReserveKillboxFence:
			fenced = true
		}
	}
	core, fences := 0, 0
	for _, t := range record.Tiers {
		if t.Remove {
			continue
		}
		switch {
		case policy.IsCorePerimeterTier(t.Name):
			core++
		case strings.HasPrefix(string(t.Name), policy.TierFencePrefix):
			fences++
		default:
			continue
		}
		if !t.Built {
			return false
		}
	}
	return core > 0 && (!fenced || fences > 0)
}

// paddockStageOf is the shell stage the containment decision reads: the yard
// closed and the marker standing in it, else what the marker's plan is doing.
func (r *RoundsAnimalContainmentPlanner) paddockStageOf(call context.Context, state ControlState, facts observation.ColonyProjection, markerBuilding bool) (policy.AnimalContainmentShellStage, policy.PaddockStep, bool, error) {
	plan, known := facts.LayoutPlan.Value()
	if !known {
		return policy.ContainmentShellNone, policy.PaddockStep{}, false, nil
	}
	snapshot := state.Snapshot
	record, found, err := r.reviewer.player.journal.LoadDefenseLayout(call, store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map})
	if err != nil {
		return 0, policy.PaddockStep{}, false, err
	}
	step, sited := paddockOf(facts)
	switch {
	case markerBuilding:
		return policy.ContainmentShellPending, step, sited, nil
	case found && sited && paddockClosed(record, plan):
		return policy.ContainmentShellComplete, step, sited, nil
	}
	return policy.ContainmentShellNone, step, sited, nil
}

// paddockOf is the projection's yard step; false while the plan, the
// construction census or the marker's size is unknown.
func paddockOf(facts observation.ColonyProjection) (policy.PaddockStep, bool) {
	plan, pk := facts.LayoutPlan.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	ground, gk := colonyGround(facts)
	if !pk || !ck || !gk {
		return policy.PaddockStep{}, false
	}
	return policy.NextPaddockStep(plan, plan.GroundWithRock(ground, naturalRock(facts)), census.Buildings, paddockMarkerShape(facts))
}

// paddockMarkerShape is the PenMarker's native shape; zero when unread.
func paddockMarkerShape(facts observation.ColonyProjection) policy.InteriorPieceDef {
	if d, found := animalContainmentDefinition(facts.Definitions, policy.PenMarkerDefinition); found {
		if size, known := d.Size.Value(); known && size.Width >= 1 && size.Height >= 1 {
			return policy.InteriorPieceDef{Def: d.Name, Size: domain.Cell{X: size.Width, Z: size.Height}}
		}
	}
	return policy.InteriorPieceDef{}
}

// stagePaddock places the one PenMarker on a free yard cell through the shared
// build side (commitBuilds). A cell the native refuses moves the marker to the
// next candidate; the marker is never placed twice (the step sees a standing
// one, and an open plan holds the pass).
func (r *RoundsAnimalContainmentPlanner) stagePaddock(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, read observation.RoundsReading, step policy.PaddockStep) (RoundsAnimalContainmentResult, error) {
	facts := read.Projection
	markerDef, ok := animalContainmentDefinition(facts.Definitions, policy.PenMarkerDefinition)
	if !ok {
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("pen_marker_definition")}, nil
	}
	avail, ak := markerDef.Available.Value()
	if !ak {
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("pen_marker_availability")}, nil
	}
	if !avail {
		return RoundsAnimalContainmentResult{Verdict: awaitingPlan("pen_marker", "unbuildable")}, nil
	}
	if len(step.Candidates) == 0 {
		return RoundsAnimalContainmentResult{Verdict: noSpace("paddock_marker")}, nil
	}
	plan, _ := facts.LayoutPlan.Value()
	shape := paddockMarkerShape(facts)
	var result RoundsBuildingResult
	for i, anchor := range step.Candidates {
		if i == maxPaddockCandidates {
			break
		}
		piece := policy.PaddockMarkerPiece(anchor, shape)
		room := policy.PlannedRoom{Role: policy.PlannedPen, Outdoor: true, Interior: policy.Rectangle{X: piece.Minimum.X, Z: piece.Minimum.Z, Width: piece.Maximum.X - piece.Minimum.X + 1, Height: piece.Maximum.Z - piece.Minimum.Z + 1}}
		var err error
		result, err = r.building.commitBuilds(call, epoch, state, review, goal, read, plan, []roomWork{{
			rr:  roomReconcile{room: room, name: "paddock-marker", reason: string(policy.PlannedPen)},
			ops: []policy.Operation{{Kind: policy.OpBuild, Pieces: []policy.WantedPiece{piece}}},
		}})
		if err != nil || result.Verdict.Refusal.Kind != WaitExistingWork {
			return RoundsAnimalContainmentResult{Verdict: result.Verdict}, err
		}
	}
	return RoundsAnimalContainmentResult{Verdict: result.Verdict}, nil
}
