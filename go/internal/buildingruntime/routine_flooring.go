package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// NewRoutineFlooringPlanner composes MaintainFlooring's building method: lay
// a role-appropriate floor on the cells native measures short of their
// room's requirement (issue #6 slice 4). Completion is the next measured
// census, not the build receipts: the review releases a room only once no
// cell of it reads deficient.
func NewRoutineFlooringPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainFlooring) {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.MaintainFlooring, definition: "WoodPlankFloor"}, nil
}

// flooringDefinitions lists every floor the policy may choose so the census
// read carries each one's availability, stats and cost list.
func (r *RoutineBuildingPlanner) flooringDefinitions() []string {
	return append([]string(nil), r.reviewer.policy.Flooring.Floors...)
}

// selectFlooring re-reviews the fresh census under the review's latch and
// maps the policy outcome onto the planner: a build resolves the floor
// definition and its cells, everything else is a reason.
func (r *RoutineBuildingPlanner) selectFlooring(facts observation.ColonyProjection, latches policy.RoutineLatches) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	p := r.reviewer.policy.Flooring
	review, err := policy.ReviewFlooring(facts.Facts.Upkeep.Flooring, facts.Rooms, latches.Flooring, p)
	if err != nil {
		return nil, "", err
	}
	if !review.Active {
		return nil, BuildingMethodNoDeficit, nil
	}
	if !review.Known {
		return nil, BuildingMethodUnknown, nil
	}
	flooring := policy.FlooringFacts{Definitions: map[string]policy.FloorDefinition{}, Stock: facts.Resources}
	for _, d := range facts.Definitions {
		flooring.Definitions[d.Name] = policy.FloorDefinition{Available: d.Available, Terrain: d.Terrain, Cleanliness: d.Cleanliness, Beauty: d.Beauty, Flammability: d.Flammability, PathCost: d.PathCost, Costs: d.Costs}
	}
	proposal, err := policy.SelectFlooringMethod(review, flooring, p)
	if err != nil {
		return nil, "", err
	}
	if clockSchedulerDebug {
		clockSchedulerLog("flooring: review=%+v definitions=%d stock=%+v proposal=%+v", review, len(flooring.Definitions), flooring.Stock, proposal)
	}
	switch proposal.Method {
	case policy.FlooringBuild:
		resolved := *r
		resolved.flooring = &proposal
		resolved.definition = proposal.Definition
		return &resolved, "", nil
	case policy.FlooringUnknown:
		return nil, BuildingMethodUnknown, nil
	case policy.FlooringNoMethod:
		return nil, BuildingMethodNoDeficit, nil
	default:
		return nil, RoutineBuildingReason(proposal.Method), nil
	}
}

// previewFlooring previews the proposal's cells in order and admits every
// one native reports legal and safe; a floor has no rotation and a
// one-cell footprint. Cells native refuses (a wall, an identical floor laid
// meanwhile) are skipped; the batch is the cells that remain.
func (r *RoutineBuildingPlanner) previewFlooring(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if r.flooring == nil || r.flooring.Method != policy.FlooringBuild {
		return nil, stock, "", ErrControl
	}
	guarded := map[domain.Cell]bool{}
	for _, c := range protected {
		guarded[c] = true
	}
	var selected []policy.Preview
	unknown := false
	for _, cell := range r.flooring.Cells {
		if guarded[cell] {
			continue
		}
		building, err := domain.NewBuilding(r.flooring.Definition, cell, domain.North, "")
		if err != nil {
			return nil, stock, "", err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(selected))), building)
		if err != nil {
			return nil, stock, "", err
		}
		preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, stock, "", err
		}
		if err = check(); err != nil {
			return nil, stock, "", err
		}
		p := preview.Preview
		if p.Action != action || !p.Snapshot.Matches(snapshot) || !p.Tick.FreshFor(facts.Identity.Tick) || !preview.Stock.Snapshot.Matches(snapshot) || !preview.Stock.Tick.FreshFor(facts.Identity.Tick) {
			return nil, stock, "", ErrControl
		}
		footprint, fk := p.Footprint.Value()
		made, mk := p.MadeFromStuff.Value()
		legal, lk := p.CanPlace.Value()
		safe, sk := p.SafeToPlace.Value()
		if !fk || !mk || !lk || !sk {
			unknown = true
			continue
		}
		if made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
			if clockSchedulerDebug {
				clockSchedulerLog("flooring: cell %v refused legal=%v safe=%v footprint=%v", cell, legal, safe, footprint)
			}
			continue
		}
		if err = mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return nil, stock, "", err
		}
		selected = append(selected, p)
	}
	if len(selected) > 0 {
		if clockSchedulerDebug {
			clockSchedulerLog("flooring: %d cells previewed for %s in room %s stock=%+v", len(selected), r.flooring.Definition, r.flooring.Room, stock.Values)
		}
		return selected, stock, "", nil
	}
	if unknown {
		return nil, stock, BuildingMethodUnknown, nil
	}
	return nil, stock, BuildingMethodNoSpace, nil
}
