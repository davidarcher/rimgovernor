package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// NewRoundsRoutesPlanner composes MaintainRoutes' building method: cut a
// door into the wall enclosing a facility no colonist can reach (issue #6
// slice 5). The deficit and its release are the native reachability census,
// never a flood fill: the latch opens only once a measured census reads the
// facility reachable by some colonist.
func NewRoundsRoutesPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainRoutes) {
		return nil, fmt.Errorf("%w: NewRoundsRoutesPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainRoutes)", ErrControl)
	}
	if _, ok := native.(observation.RoundsSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsRoutesPlanner: !ok", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainRoutes, definition: reviewer.policy.Routes.Door}, nil
}

// routesDefinitions lists the door the policy opens breaches with so the
// census read carries its availability.
func (r *RoundsBuildingPlanner) routesDefinitions() []string {
	return []string{r.reviewer.policy.Routes.Door}
}

// selectRoutes re-reviews the fresh census under the review's latch and maps
// the policy outcome onto the planner.
func (r *RoundsBuildingPlanner) selectRoutes(facts observation.ColonyProjection, latches policy.RoundsLatches) (*RoundsBuildingPlanner, Verdict, error) {
	p := r.reviewer.policy.Routes
	review, err := policy.ReviewRoutes(facts.Facts.Upkeep.Routes, latches.Routes, p)
	if err != nil {
		return nil, Verdict{}, err
	}
	if !review.Active {
		return nil, BuildingReasonNoDeficit, nil
	}
	if !review.Known {
		return nil, fieldUnavailable("routes"), nil
	}
	routes := policy.RoutesFacts{}
	for _, d := range facts.Definitions {
		if d.Name == p.Door {
			routes.DoorAvailable = d.Available
		}
	}
	proposal, err := policy.SelectRoutesMethod(review, routes, p)
	if err != nil {
		return nil, Verdict{}, err
	}
	switch proposal.Method {
	case policy.RoutesBuild:
		resolved := *r
		resolved.routes = &proposal
		resolved.definition = proposal.Definition
		return &resolved, Verdict{}, nil
	case policy.RoutesUnknown:
		// The review is known by here, so the door's availability is what
		// the game did not report.
		return nil, fieldUnavailable(p.Door + "_availability"), nil
	case policy.RoutesNoMethod:
		return nil, BuildingReasonNoDeficit, nil
	case policy.RoutesDoorUnavailable:
		door, _ := definitionRow(facts, p.Door)
		return nil, definitionAvailability(door), nil
	case policy.RoutesDoorPending:
		return nil, awaitingPlan("route_door", "ordered"), nil
	case policy.RoutesNoBreach:
		return nil, noSpace("route_breach"), nil
	default:
		return nil, awaitingMethod(proposal.Method), nil
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
func (r *RoundsBuildingPlanner) previewRoutes(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if r.routes == nil || r.routes.Method != policy.RoutesBuild {
		return nil, stock, Verdict{}, fmt.Errorf("%w: previewRoutes: r.routes == nil || r.routes.Method != policy.RoutesBuild", ErrControl)
	}
	guarded := map[domain.Cell]bool{}
	for _, c := range protected {
		guarded[c] = true
	}
	missing := ""
	for _, cell := range r.routes.Breaches {
		if guarded[cell] {
			continue
		}
		for _, rotation := range []domain.Rotation{domain.North, domain.East} {
			building, err := domain.NewBuilding(r.routes.Definition, cell, rotation, r.routes.Stuff)
			if err != nil {
				return nil, stock, Verdict{}, err
			}
			action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), building)
			if err != nil {
				return nil, stock, Verdict{}, err
			}
			preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
			if err != nil {
				return nil, stock, Verdict{}, err
			}
			p := preview.Preview
			footprint, fk := p.Footprint.Value()
			made, mk := p.MadeFromStuff.Value()
			legal, lk := p.CanPlace.Value()
			safe, sk := p.SafeToPlace.Value()
			if !fk || !mk || !lk || !sk {
				if missing == "" {
					missing = firstUnknown([]string{"preview_footprint", "preview_made_from_stuff", "preview_can_place", "preview_safe_to_place"}, fk, mk, lk, sk)
				}
				continue
			}
			if !safe && deliberateBreach(p) {
				safe = true
				p.SafeToPlace = domain.Known(true)
			}
			if !made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
				continue
			}
			if err = mergeRoundsStock(&stock, preview.Stock, true); err != nil {
				return nil, stock, Verdict{}, err
			}
			return []policy.Preview{p}, stock, Verdict{}, nil
		}
	}
	if missing != "" {
		return nil, stock, fieldUnavailable(missing), nil
	}
	return nil, stock, noSpace("breach_door_site"), nil
}

// firstUnknown names the first field whose known flag is false.
func firstUnknown(names []string, known ...bool) string {
	for i, ok := range known {
		if !ok {
			return names[i]
		}
	}
	return ""
}
