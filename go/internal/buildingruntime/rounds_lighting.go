package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// NewRoundsLightingPlanner composes MaintainLighting's building method:
// light the roofed work cells native measures dark by placing an affordable
// lamp beside them (issue #6 slice 3). Completion is the next measured
// census, not the build receipt: the review releases a bench only once its
// interaction cell reads lit.
func NewRoundsLightingPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainLighting) {
		return nil, fmt.Errorf("%w: NewRoundsLightingPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainLighting)", ErrControl)
	}
	if _, ok := native.(observation.RoundsSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsLightingPlanner: !ok", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainLighting, definition: "TorchLamp"}, nil
}

// lightingDefinitions lists every lamp the policy may choose so the census
// read carries each one's availability.
func (r *RoundsBuildingPlanner) lightingDefinitions() []string {
	var out []string
	for _, lamp := range r.reviewer.policy.Lighting.Lamps {
		out = append(out, lamp.Name)
	}
	return out
}

// selectLighting re-reviews the fresh census under the review's latch and
// maps the policy outcome onto the planner: a build resolves the lamp
// definition and its candidate cells, everything else is a reason.
func (r *RoundsBuildingPlanner) selectLighting(facts observation.ColonyProjection, latches policy.RoundsLatches) (*RoundsBuildingPlanner, Verdict, error) {
	p := r.reviewer.policy.Lighting
	review, err := policy.ReviewLighting(facts.Facts.Upkeep.Lighting, latches.Lighting, p, policy.SkyDarkHold(facts.Facts.DisasterConditions, facts.Facts.OutdoorsDark))
	if err != nil {
		return nil, Verdict{}, err
	}
	if !review.Active {
		return nil, BuildingReasonNoDeficit, nil
	}
	if !review.Known {
		return nil, fieldUnavailable("lighting"), nil
	}
	lighting := policy.LightingFacts{Cells: facts.Cells, Available: map[string]domain.Fact[bool]{}, PoweredSource: poweredSource(facts), Styled: lampStyle(facts)}
	if rooms, known := facts.Rooms.Value(); known {
		lighting.Rooms = rooms.Rooms
	}
	for _, d := range facts.Definitions {
		lighting.Available[d.Name] = d.Available
	}
	proposal, err := policy.SelectLightingMethod(review, facts.Facts.Upkeep.Lighting, lighting, p)
	if err != nil {
		return nil, Verdict{}, err
	}
	if clockDebug() {
		clockSchedulerLog("lighting: review=%+v rooms=%d cells=%d source=%+v proposal=%+v", review, len(lighting.Rooms), len(lighting.Cells), lighting.PoweredSource, proposal)
	}
	switch proposal.Method {
	case policy.LightingBuild:
		resolved := *r
		resolved.lighting = &proposal
		resolved.definition = proposal.Definition
		return &resolved, Verdict{}, nil
	case policy.LightingUnknown:
		return nil, fieldUnavailable("lighting"), nil
	case policy.LightingNoMethod:
		return nil, BuildingReasonNoDeficit, nil
	default:
		return nil, awaitingMethod(proposal.Method), nil
	}
}

// previewLighting previews the policy's candidate cells nearest first and
// admits the first one native reports legal and safe; a lamp has no
// rotation, so only the cell varies.
func (r *RoundsBuildingPlanner) previewLighting(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if r.lighting == nil || r.lighting.Method != policy.LightingBuild {
		return nil, stock, Verdict{}, fmt.Errorf("%w: previewLighting: r.lighting == nil || r.lighting.Method != policy.LightingBuild", ErrControl)
	}
	guarded := map[domain.Cell]bool{}
	for _, c := range protected {
		guarded[c] = true
	}
	unknown := false
	for _, cell := range r.lighting.Cells {
		if guarded[cell] {
			continue
		}
		building, err := domain.NewBuilding(r.lighting.Definition, cell, domain.North, "")
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
			unknown = true
			continue
		}
		if made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
			if clockDebug() {
				clockSchedulerLog("lighting: cell %v refused legal=%v safe=%v footprint=%v", cell, legal, safe, footprint)
			}
			continue
		}
		if err = mergeRoundsStock(&stock, preview.Stock, true); err != nil {
			return nil, stock, Verdict{}, err
		}
		if clockDebug() {
			clockSchedulerLog("lighting: preview cell=%v costs=%+v stock=%+v", cell, p.Costs, stock.Values)
		}
		return []policy.Preview{p}, stock, Verdict{}, nil
	}
	if unknown {
		return nil, stock, fieldUnavailable("lighting_preview"), nil
	}
	return nil, stock, noSpace("light_cell"), nil
}
