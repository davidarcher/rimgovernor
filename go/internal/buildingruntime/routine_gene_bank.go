package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// geneBankSiteTries bounds the free footprints a bank previews.
const geneBankSiteTries = 8

// RoutineGeneBankPlanner composes MaintainGeneBank's method (#1933): one gene
// bank, found by the genepack container comp in the catalog and sited on the
// free footprint nearest the colony's gene assemblers (a bank links to an
// assembler within a dozen cells), else its other banks, else the production
// district, on the first footprint native previews as legal, safe and
// reachable. The goal settles on the game's verdict (every pack has a slot),
// never on the plan; powering the bank is EnsureBasicPower's.
type RoutineGeneBankPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineBuildingSource
}

func NewRoutineGeneBankPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineGeneBankPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainGeneBank) {
		return nil, fmt.Errorf("%w: NewRoutineGeneBankPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainGeneBank)", ErrControl)
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, fmt.Errorf("%w: NewRoutineGeneBankPlanner: !ok", ErrControl)
	}
	return &RoutineGeneBankPlanner{reviewer: reviewer, native: native}, nil
}

// geneBankDefinitions are the available gene bank definitions in catalog
// order (sorted by name), resolved into the projection.
func geneBankDefinitions(read *observation.RoutineReading) ([]observation.PlanningDefinition, error) {
	if read.Frame.Catalog == nil {
		return nil, nil
	}
	names, err := read.Frame.Catalog.GeneBanks()
	if err != nil {
		return nil, err
	}
	if err := read.Projection.AddDefinitions(read.Frame, names); err != nil {
		return nil, err
	}
	var out []observation.PlanningDefinition
	for _, d := range read.Projection.Definitions {
		if available, ok := d.Available.Value(); ok && available && slices.Contains(names, d.Name) {
			out = append(out, d)
		}
	}
	return out, nil
}

// geneBankAnchor is where the site search starts: beside a gene assembler,
// else a standing bank, else the nearest free workshop room.
func geneBankAnchor(facts observation.ColonyProjection, biotech observation.BiotechColony) domain.Cell {
	if len(biotech.GeneAssemblers) > 0 {
		return biotech.GeneAssemblers[0].Position
	}
	if len(biotech.GeneBanks) > 0 {
		return biotech.GeneBanks[0].Position
	}
	return roomAnchor(facts, policy.ModuleWorkshop, facts.Center)
}

func (r *RoutineGeneBankPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineBuildingResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineBuildingResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineBuildingResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainGeneBank)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !workable {
		return RoutineBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// The bank competes for the bounded development capacity like the other
	// priority>=3 autopilot goals.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainGeneBank && row.Selected
	}
	if !selected {
		return RoutineBuildingResult{Verdict: awaitingSlot(string(policy.MaintainGeneBank))}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineBuildingResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	expected, err := stepScope(call, r.native)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineBuildingResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	read, err := r.reviewer.observeRooms(call, r.native.(observation.RoutineSource), expected, claims)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	f := read.Projection
	owed, known := f.Facts.GeneBankOwed.Value()
	if !known {
		return RoutineBuildingResult{Verdict: fieldUnavailable("gene_banks")}, nil
	}
	if !owed {
		return RoutineBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	biotech, biotechKnown := f.Biotech.Value()
	if !biotechKnown {
		return RoutineBuildingResult{Verdict: fieldUnavailable("biotech")}, nil
	}
	defs, err := geneBankDefinitions(&read)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if len(defs) == 0 {
		return RoutineBuildingResult{Verdict: fieldUnavailable("gene_bank_definition")}, nil
	}
	definition := defs[0].Name
	// A blueprint or frame a retired plan left standing is the bank still
	// being built, not a bank to stage again.
	if definitionIntentStanding(defs, f.Facts.ConstructionClaims, f.Facts.CurrentConstruction) {
		return RoutineBuildingResult{Verdict: BuildingReasonExistingWork}, nil
	}
	method := domain.MethodID(fmt.Sprintf("gene-bank-%s-%d", definition, len(biotech.GeneBanks)))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Verdict: waitFor(WaitMethodUsed, "gene_bank_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, err
	}
	size, sizeKnown := defs[0].Size.Value()
	if !sizeKnown || size.Width <= 0 || size.Height <= 0 {
		return RoutineBuildingResult{Verdict: fieldUnavailable("gene_bank_size")}, nil
	}
	planID := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan, snapshot.Revision = planID, 1
	preview := func(anchor domain.Cell) (policy.Preview, policy.StockObservation, domain.Action, error) {
		building, err := domain.NewBuilding(definition, anchor, domain.North, "")
		if err != nil {
			return policy.Preview{}, policy.StockObservation{}, domain.Action{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(string(planID)+"-0"), building)
		if err != nil {
			return policy.Preview{}, policy.StockObservation{}, domain.Action{}, err
		}
		got, _, err := r.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return policy.Preview{}, policy.StockObservation{}, domain.Action{}, err
		}
		return got.Preview, got.Stock, action, nil
	}
	sites, err := policy.FreeSites(policy.PenEnclosureRequest{Bounds: f.Bounds, Anchor: geneBankAnchor(f, biotech), Cells: f.Cells}, size.Width, size.Height)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	unknown := false
	for i, site := range sites {
		if i == geneBankSiteTries {
			break
		}
		anchor := policy.AnchorForRect(site, domain.Cell{X: size.Width, Z: size.Height}, domain.North)
		pv, stock, action, err := preview(anchor)
		if err != nil {
			return RoutineBuildingResult{}, err
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
		if made || !legal || !safe || !reachable || !footprintIsRect(footprint, site) {
			continue
		}
		plan, err := domain.NewPlan(planID, 1, []domain.Action{action})
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoutineBuildingResult{}, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoutineBuildingResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: goal, Method: method, Plan: plan, Current: snapshot, Tick: f.Identity.Tick, Bounds: domain.Known(f.Bounds), Stock: stock, Previews: []policy.Preview{pv}, Purpose: policy.Routine})
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if !decision.Admitted {
			return RoutineBuildingResult{Verdict: admissionRefused(decision), Decision: decision}, nil
		}
		return RoutineBuildingResult{Verdict: BuildingReasonAdmitted, Decision: decision}, nil
	}
	if unknown {
		return RoutineBuildingResult{Verdict: fieldUnavailable("gene_bank_preview")}, nil
	}
	return RoutineBuildingResult{Verdict: noSpace("gene_bank_cell")}, nil
}
