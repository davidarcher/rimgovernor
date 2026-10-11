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

// maxShelvesPerZone bounds the shelves a zone may ever be given.
const maxShelvesPerZone = 8

// shelfMethod keys a zone shelf under the stockpiles goal; it is epoch-free,
// so a shelf placed under one goal episode is still found in the next.
func shelfMethod(zone string, index int) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("storage-shelf-%s-%d", zone, index))
}

// shelve is the stockpiles planner's shelf upgrade: the review's next
// warehouse shelf, placed once no zone edit stands. policy.NextShelfStep
// picked the step (one open shelf at a time, a third of the footprint at
// most); MaintainStockpiles configures every built shelf like its zone
// (policy.StockpileShelfPatch, role shelf:<buildingID>).
func (r *RoundsStockpilePlanner) shelve(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, reading observation.RoundsReading, step policy.ShelfStep) (RoundsStockpileResult, error) {
	if r.building == nil {
		return RoundsStockpileResult{Verdict: fieldUnavailable("building_source")}, nil
	}
	_, index, err := zoneShelves(call, r.reviewer.player.journal, goal.Standard.ID, step.Zone.Zone, reading.Projection.Facts.CurrentConstruction)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if index >= maxShelvesPerZone {
		return RoundsStockpileResult{Verdict: refuse(policy.CauseRetriesSpent, "maxShelvesPerZone", "")}, nil
	}
	return r.buildShelf(call, epoch, state, review, goal, reading, step, index)
}

// zoneShelves reads back the zone's shelf plans in index order: the
// shelves built or in flight, and the next free index.
func zoneShelves(ctx context.Context, journal *store.Store, goal domain.StandardID, zone string, census domain.Fact[policy.CurrentConstruction]) ([]policy.ShelfRecord, int, error) {
	var out []policy.ShelfRecord
	for index := 0; index < maxShelvesPerZone; index++ {
		id, err := journal.LatestMethodPlan(ctx, goal, shelfMethod(zone, index))
		if errors.Is(err, store.ErrNotFound) {
			return out, index, nil
		}
		if err != nil {
			return nil, 0, err
		}
		plan, err := journal.LoadPlan(ctx, id)
		if err != nil {
			return nil, 0, err
		}
		record := policy.ShelfRecord{Zone: zone}
		for _, progress := range plan.Progress {
			b, ok := progress.Action().Building()
			if !ok {
				continue
			}
			rect := policy.OccupiedRect(b.Cell(), domain.Cell{X: 2, Z: 1}, b.Rotation())
			record.Cells = nil
			for x := rect.X; x < rect.X+rect.Width; x++ {
				for z := rect.Z; z < rect.Z+rect.Height; z++ {
					record.Cells = append(record.Cells, domain.Cell{X: x, Z: z})
				}
			}
		}
		if store.PlanWorkOpen(plan, census) {
			record.Open = true
			out = append(out, record)
			continue
		}
		if !policy.PlanBuilt(plan.Progress, census) {
			continue
		}
		current, _ := census.Value()
		for _, progress := range plan.Progress {
			if b, ok := progress.Action().Building(); ok {
				if id, built := current.Built(b); built {
					record.Building = id
				}
			}
		}
		if record.Building == "" {
			continue
		}
		out = append(out, record)
	}
	return out, maxShelvesPerZone, nil
}

// build previews the step's candidate sites in turn and admits the first
// native accepts as the zone's next shelf.
func (r *RoundsStockpilePlanner) buildShelf(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, reading observation.RoundsReading, step policy.ShelfStep, index int) (RoundsStockpileResult, error) {
	p := r.reviewer.player
	facts := reading.Projection
	stuff := facts.BuildStuff(policy.ShelfDefinition)
	snapshot := state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: build: p.session.State() != state", ErrControl)
		}
		return nil
	}
	building := &RoundsBuildingPlanner{reviewer: r.reviewer, native: r.building.native, concern: policy.MaintainStockpiles, definition: policy.ShelfDefinition}
	for _, piece := range step.Pieces {
		if err := check(); err != nil {
			return RoundsStockpileResult{}, err
		}
		value, err := domain.NewBuilding(piece.Def, piece.Anchor(), piece.Rot, stuff)
		if err != nil {
			return RoundsStockpileResult{}, err
		}
		// A footprint on rock or fogged mountain is mined and the shelf
		// built in one method through the shared rock step.
		var planned []policy.RoleCell
		for x := piece.Rect.X; x < piece.Rect.X+piece.Rect.Width; x++ {
			for z := piece.Rect.Z; z < piece.Rect.Z+piece.Rect.Height; z++ {
				planned = append(planned, policy.RoleCell{Cell: domain.Cell{X: x, Z: z}, Role: policy.RockNeedsFloor})
			}
		}
		if dig := policy.RockStep(planned, facts.Cells).Dig; len(dig) > 0 {
			access, ok := policy.RockAccess(dig, facts.Cells)
			if !ok {
				return RoundsStockpileResult{Verdict: rockNotDug(policy.ShelfDefinition, "no_open_cell_beside_footprint")}, nil
			}
			method := shelfMethod(step.Zone.Zone, index)
			dug, handled, err := building.admitRockStep(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading.ColonyReading}, planned, access, method, []domain.Building{value}, check)
			if err != nil {
				return RoundsStockpileResult{}, err
			}
			if handled {
				out := RoundsStockpileResult{Verdict: dug.Verdict}
				for _, m := range dug.Decision.Standard.Methods {
					if m.Method == method {
						out.Plan = m.Plan
					}
				}
				return out, nil
			}
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), value, domain.TierSustain)
		if err != nil {
			return RoundsStockpileResult{}, err
		}
		preview, _, err := r.building.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoundsStockpileResult{}, err
		}
		v := preview.Preview
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !ck || !can || !sk || !safe {
			continue
		}
		stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
		if err := mergeRoundsStock(&stock, preview.Stock, true); err != nil {
			return RoundsStockpileResult{}, err
		}
		method := shelfMethod(step.Zone.Zone, index)
		result, err := building.admitPreviews(call, epoch, roundsAdmission{state: state, review: review, owner: goal, facts: facts, method: method, reason: "shelf for zone " + step.Zone.Zone, snapshot: snapshot, selected: []policy.Preview{v}, stock: stock, purpose: policy.Rounds})
		if err != nil || result.Verdict != BuildingReasonAdmitted {
			return RoundsStockpileResult{Verdict: result.Verdict}, err
		}
		return RoundsStockpileResult{Verdict: BuildingReasonAdmitted, Plan: snapshot.Plan}, nil
	}
	return RoundsStockpileResult{Verdict: noSpace("storage_shelf")}, nil
}
