package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// NewRoutineRoutesPlanner composes MaintainRoutes' building method: cut a
// door into the wall enclosing a facility no colonist can reach (issue #6
// slice 5). The deficit and its release are the native reachability census,
// never a flood fill: the latch opens only once a measured census reads the
// facility reachable by some colonist.
func NewRoutineRoutesPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainRoutes) {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.MaintainRoutes, definition: reviewer.policy.Routes.Door}, nil
}

// routesDefinitions lists the door the policy opens breaches with so the
// census read carries its availability.
func (r *RoutineBuildingPlanner) routesDefinitions() []string {
	return []string{r.reviewer.policy.Routes.Door}
}

// selectRoutes re-reviews the fresh census under the review's latch and maps
// the policy outcome onto the planner.
func (r *RoutineBuildingPlanner) selectRoutes(facts observation.ColonyProjection, latches policy.RoutineLatches) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	p := r.reviewer.policy.Routes
	review, err := policy.ReviewRoutes(facts.Facts.Upkeep.Routes, latches.Routes, p)
	if err != nil {
		return nil, "", err
	}
	if !review.Active {
		return nil, BuildingMethodNoDeficit, nil
	}
	if !review.Known {
		return nil, BuildingMethodUnknown, nil
	}
	routes := policy.RoutesFacts{}
	for _, d := range facts.Definitions {
		if d.Name == p.Door {
			routes.DoorAvailable = d.Available
		}
	}
	proposal, err := policy.SelectRoutesMethod(review, routes, p)
	if err != nil {
		return nil, "", err
	}
	if clockDebug() {
		clockSchedulerLog("routes: review=%+v door=%+v proposal=%+v", review, routes.DoorAvailable, proposal)
	}
	switch proposal.Method {
	case policy.RoutesBuild:
		resolved := *r
		resolved.routes = &proposal
		resolved.definition = proposal.Definition
		return &resolved, "", nil
	case policy.RoutesUnknown:
		return nil, BuildingMethodUnknown, nil
	case policy.RoutesNoMethod:
		return nil, BuildingMethodNoDeficit, nil
	default:
		return nil, RoutineBuildingReason(proposal.Method), nil
	}
}

// deliberateBreach reports a preview whose only disturbance is the wall the
// door replaces: one wiped building, no blueprint, frame or cancelled work.
// Native reports a door over a wall as unsafe because the wall is wiped;
// here the wipe is the method, so the planner marks the placement safe
// itself. Any other disturbance keeps native's verdict.
func deliberateBreach(p policy.Preview) bool {
	if len(p.Blockers) != 1 {
		return false
	}
	b := p.Blockers[0]
	return b.Category == "Building" && b.Wiped && !b.Blueprint && !b.Frame && !b.Cancelled
}

// previewRoutes previews the proposal's breach cells nearest first and admits
// the first one native reports legal with a one-cell footprint on the wall
// cell; one door is enough to open the room, so the batch is a single
// action. A door rotates with its wall, so both orientations are tried.
func (r *RoutineBuildingPlanner) previewRoutes(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if r.routes == nil || r.routes.Method != policy.RoutesBuild {
		return nil, stock, "", ErrControl
	}
	guarded := map[domain.Cell]bool{}
	for _, c := range protected {
		guarded[c] = true
	}
	unknown := false
	for _, cell := range r.routes.Breaches {
		if guarded[cell] {
			continue
		}
		for _, rotation := range []domain.Rotation{domain.North, domain.East} {
			building, err := domain.NewBuilding(r.routes.Definition, cell, rotation, r.routes.Stuff)
			if err != nil {
				return nil, stock, "", err
			}
			action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), building)
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
			if !safe && deliberateBreach(p) {
				safe = true
				p.SafeToPlace = domain.Known(true)
			}
			if !made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
				if clockDebug() {
					clockSchedulerLog("routes: breach %v rotation %v refused legal=%v safe=%v footprint=%v blockers=%+v", cell, rotation, legal, safe, footprint, p.Blockers)
				}
				continue
			}
			if err = mergeRoutineStock(&stock, preview.Stock, true); err != nil {
				return nil, stock, "", err
			}
			if clockDebug() {
				clockSchedulerLog("routes: door %s on breach %v for facility %s stock=%+v", r.routes.Definition, cell, r.routes.Facility, stock.Values)
			}
			return []policy.Preview{p}, stock, "", nil
		}
	}
	if unknown {
		return nil, stock, BuildingMethodUnknown, nil
	}
	return nil, stock, BuildingMethodNoSpace, nil
}
