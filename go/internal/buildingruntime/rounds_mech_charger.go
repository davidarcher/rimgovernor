package buildingruntime

import (
	"context"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsMechChargerPlanner composes EnsureMechCharger's method: one
// mech charger, found by the catalog's mech_charger flag and sited by the
// polluting-machine rule (policy.MechChargerSites over PollutionSites) on the
// first footprint native previews as legal, safe and reachable. The goal
// settles on the game's verdict (a charger stands idle), never on the plan.
type RoundsMechChargerPlanner struct {
	reviewer *Rounder
	native   RoundsBuildingSource
}

func NewRoundsMechChargerPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsMechChargerPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.EnsureMechCharger) {
		return nil, fmt.Errorf("%w: NewRoundsMechChargerPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(policy.EnsureMechCharger)", ErrControl)
	}
	if _, ok := native.(observation.RoundsSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsMechChargerPlanner: !ok", ErrControl)
	}
	return &RoundsMechChargerPlanner{reviewer: reviewer, native: native}, nil
}

// mechChargerDefinitions are the available mech charger definitions in
// catalog order (sorted by name), resolved into the projection.
func mechChargerDefinitions(read *observation.RoundsReading) ([]observation.PlanningDefinition, error) {
	if read.Frame.Catalog == nil {
		return nil, nil
	}
	names, err := read.Frame.Catalog.MechChargers()
	if err != nil {
		return nil, err
	}
	if err := read.Projection.AddDefinitions(read.Frame, names); err != nil {
		return nil, err
	}
	var out []observation.PlanningDefinition
	for _, d := range observation.MechChargerDefs(read.Projection.Definitions) {
		if available, ok := d.Available.Value(); ok && available {
			out = append(out, d)
		}
	}
	return out, nil
}

func (r *RoundsMechChargerPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsBuildingResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsBuildingResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsBuildingResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.EnsureMechCharger)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !workable {
		return RoundsBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	expected, err := stepScope(call, r.native)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsBuildingResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	read, err := r.reviewer.observeRooms(call, r.native.(observation.RoundsSource), expected, claims)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	f := read.Projection
	owed, known := f.Facts.MechChargerOwed.Value()
	if !known {
		return RoundsBuildingResult{Verdict: fieldUnavailable("mech_chargers")}, nil
	}
	if !owed {
		return RoundsBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	biotech, biotechKnown := f.Biotech.Value()
	if !biotechKnown {
		return RoundsBuildingResult{Verdict: fieldUnavailable("biotech")}, nil
	}
	defs, err := mechChargerDefinitions(&read)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if len(defs) == 0 {
		return RoundsBuildingResult{Verdict: fieldUnavailable("mech_charger_definition")}, nil
	}
	definition := defs[0].Name
	// A blueprint or frame a retired plan left standing is the charger
	// still being built, not a charger to stage again.
	if definitionIntentStanding(defs, f.Facts.ConstructionClaims, f.Facts.CurrentConstruction) {
		return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, nil
	}
	method := domain.MethodID(fmt.Sprintf("mech-charger-%s-%d", definition, len(biotech.Chargers)))
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "mech_charger_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsBuildingResult{}, err
	}
	planID := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan, snapshot.Revision = planID, 1
	preview := func(anchor domain.Cell) (policy.Preview, policy.StockObservation, domain.Action, error) {
		building, err := domain.NewBuilding(definition, anchor, domain.North, "")
		if err != nil {
			return policy.Preview{}, policy.StockObservation{}, domain.Action{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(string(planID)+"-0"), building, domain.TierExpand)
		if err != nil {
			return policy.Preview{}, policy.StockObservation{}, domain.Action{}, err
		}
		got, _, err := r.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return policy.Preview{}, policy.StockObservation{}, domain.Action{}, err
		}
		return got.Preview, got.Stock, action, nil
	}
	size, sizeKnown := defs[0].Size.Value()
	if !sizeKnown || size.Width <= 0 || size.Height <= 0 {
		return RoundsBuildingResult{Verdict: fieldUnavailable("mech_charger_size")}, nil
	}
	siteFacts := policy.MechChargerSiteFacts{Bounds: f.Bounds, Cells: f.Cells, FieldZones: map[string]bool{}, Rooms: f.Rooms}
	if fields, ok := f.FoodFields.Value(); ok {
		for _, field := range fields {
			siteFacts.FieldZones[field.ID] = true
		}
	}
	for _, atomizer := range biotech.Atomizers {
		siteFacts.Disposal = append(siteFacts.Disposal, atomizer.Position)
	}
	sites, err := policy.MechChargerSites(siteFacts, size.Width, size.Height)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	unknown := false
	for _, site := range sites {
		anchor := policy.AnchorForRect(site.Site, domain.Cell{X: size.Width, Z: size.Height}, domain.North)
		pv, stock, action, err := preview(anchor)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		footprint, fk := pv.Footprint.Value()
		made, mk := pv.MadeFromStuff.Value()
		legal, lk := pv.CanPlace.Value()
		safe, ssk := pv.SafeToPlace.Value()
		reachable, rk := pv.WatchCellsAccessible.Value()
		if !fk || !mk || !lk || !ssk || !rk {
			unknown = true
			continue
		}
		if made || !legal || !safe || !reachable || !footprintIsRect(footprint, site.Site) {
			continue
		}
		plan, err := domain.NewPlan(planID, 1, []domain.Action{action})
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoundsBuildingResult{}, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoundsBuildingResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: goal, Method: method, Plan: plan, Current: snapshot, Tick: f.Identity.Tick, Bounds: domain.Known(f.Bounds), Stock: stock, Previews: []policy.Preview{pv}, Purpose: policy.Rounds})
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		if !decision.Admitted {
			return RoundsBuildingResult{Verdict: admissionRefused(decision), Decision: decision}, nil
		}
		return RoundsBuildingResult{Verdict: BuildingReasonAdmitted, Decision: decision}, nil
	}
	if unknown {
		return RoundsBuildingResult{Verdict: fieldUnavailable("mech_charger_preview")}, nil
	}
	return RoundsBuildingResult{Verdict: noSpace("charger_cell")}, nil
}

// footprintIsRect reports whether the footprint is exactly the rectangle's cells.
func footprintIsRect(footprint []domain.Cell, r policy.Rectangle) bool {
	if int32(len(footprint)) != r.Width*r.Height {
		return false
	}
	for _, c := range footprint {
		if c.X < r.X || c.X >= r.X+r.Width || c.Z < r.Z || c.Z >= r.Z+r.Height {
			return false
		}
	}
	return true
}

// definitionIntentStanding is true when a complete census shows a blueprint
// or frame of any def in defs placed by any recorded claim, or a standing
// building the read has not yet counted (a mech charger, a gene bank).
func definitionIntentStanding(defs []observation.PlanningDefinition, claims domain.Fact[[]policy.ConstructionClaim], observed domain.Fact[policy.CurrentConstruction]) bool {
	census, known := observed.Value()
	if !known || !census.Colony {
		return false
	}
	charger := map[string]bool{}
	for _, d := range defs {
		charger[d.Name] = true
	}
	history, _ := claims.Value()
	for _, claim := range history {
		if charger[claim.Building.Definition()] && policy.WorkOpen(claim.Building, observed) == policy.BuildingOpen {
			return true
		}
	}
	return false
}
