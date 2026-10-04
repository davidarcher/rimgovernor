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

// RoutineStorageShelvesPlanner (#721) places shelves inside the stockpiles
// a method created -- the general store and the ingredient zones
// MaintainResource created before the storage planner took them over. Each shelf is a method of the goal that created its zone, taken
// only while that goal is active and selected; policy.NextShelfStep picks
// the step (one open shelf at a time, a third of the footprint at most).
// MaintainStockpiles configures every built shelf like its zone
// (policy.StockpileShelfPatch, role shelf:<buildingID>).
type RoutineStorageShelvesPlanner struct {
	reviewer *Rounder
	native   RoutineStorageShelvesSource
}

// RoutineStorageShelvesSource is the room census and the building preview.
type RoutineStorageShelvesSource interface {
	RoutineBuildingSource
}

type RoutineStorageShelvesResult struct {
	Verdict
	Plan domain.PlanID
}

// shelfGoals are the goals whose stockpiles get shelves: SecureSupplies'
// general store and MaintainResource's earlier ingredient zones.
// Food storage (meal shelves, freezers) is planned by its own goals.
var shelfGoals = map[policy.ConcernID]bool{policy.MaintainResource: true}

// maxShelvesPerZone bounds the shelves a zone may ever be given.
const maxShelvesPerZone = 8

func NewRoutineStorageShelvesPlanner(reviewer *Rounder, native RoutineStorageShelvesSource) (*RoutineStorageShelvesPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineStorageShelvesPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, fmt.Errorf("%w: NewRoutineStorageShelvesPlanner: !ok", ErrControl)
	}
	return &RoutineStorageShelvesPlanner{reviewer: reviewer, native: native}, nil
}

// shelfMethod keys a zone shelf under the zone's goal; it is epoch-free,
// so a shelf placed under one goal episode is still found in the next.
func shelfMethod(zone string, index int) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("storage-shelf-%s-%d", zone, index))
}

func (r *RoutineStorageShelvesPlanner) step(call, epoch context.Context) (RoutineStorageShelvesResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineStorageShelvesResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineStorageShelvesResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineStorageShelvesResult{Verdict: BuildingReasonNoReview}, nil
	}
	selected := map[domain.ConcernID]policy.ConcernID{}
	for _, binding := range review.Goals {
		for _, row := range review.Development.Rows {
			if row.Goal == binding.Need && row.Selected && shelfGoals[binding.Need] {
				selected[binding.Goal] = binding.Need
			}
		}
	}
	claims, err := p.journal.ZoneClaims(call, state.Snapshot, review.Tick)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	owned, known := claims.Value()
	if !known {
		return RoutineStorageShelvesResult{Verdict: fieldUnavailable("shelf_claims")}, nil
	}
	goals := map[domain.ConcernID]store.StandardState{}
	var zones []store.OwnedZone
	for _, z := range owned {
		if z.Kind != domain.StockpileZone || selected[z.Goal] == "" || z.Priority == domain.LowPriority {
			continue
		}
		if _, loaded := goals[z.Goal]; !loaded {
			g, err := p.journal.LoadStandard(call, z.Goal)
			if err != nil {
				return RoutineStorageShelvesResult{}, err
			}
			goals[z.Goal] = g
		}
		if g := goals[z.Goal].Standard; g.Status == domain.StandardOpen && g.Need == domain.NeedDeficit {
			zones = append(zones, z)
		}
	}
	if len(zones) == 0 {
		return RoutineStorageShelvesResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineStorageShelvesResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeRooms(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim](), policy.ShelfDefinition)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	facts := reading.Projection
	request := policy.ShelfRequest{Cells: facts.Cells}
	for _, d := range facts.Definitions {
		if d.Name == policy.ShelfDefinition {
			request.Available, _ = d.Available.Value()
		}
	}
	current := map[string][]domain.Cell{}
	for _, cell := range facts.Cells {
		if id, ok := cell.ZoneID.Value(); ok && id != "" {
			current[id] = append(current[id], cell.Cell)
		}
	}
	owner := map[string]store.OwnedZone{}
	next := map[string]int{}
	for _, z := range zones {
		cells := current[z.ID]
		if len(cells) == 0 {
			continue
		}
		owner[z.ID] = z
		request.Zones = append(request.Zones, policy.ShelfZone{Zone: z.ID, Cells: cells, Filter: z.Filter, Priority: z.Priority})
		shelves, index, err := zoneShelves(call, p.journal, z.Goal, z.ID, facts.Facts.CurrentConstruction)
		if err != nil {
			return RoutineStorageShelvesResult{}, err
		}
		next[z.ID] = index
		request.Shelves = append(request.Shelves, shelves...)
	}
	step := policy.NextShelfStep(request)
	if step.Kind == policy.ShelfBuild {
		z := owner[step.Zone.Zone]
		if next[z.ID] >= maxShelvesPerZone {
			return RoutineStorageShelvesResult{Verdict: refuse(RefusalRetriesSpent, "maxShelvesPerZone", "")}, nil
		}
		return r.build(call, epoch, state, review, goals[z.Goal], selected[z.Goal], reading, step, next[z.ID])
	}
	for _, shelf := range request.Shelves {
		if shelf.Open {
			return RoutineStorageShelvesResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	if !request.Available {
		return RoutineStorageShelvesResult{Verdict: awaitingPlan("shelf", "unbuildable")}, nil
	}
	return RoutineStorageShelvesResult{Verdict: BuildingReasonNoDeficit}, nil
}

// zoneShelves reads back the zone's shelf plans in index order: the
// shelves built or in flight, and the next free index.
func zoneShelves(ctx context.Context, journal *store.Store, goal domain.ConcernID, zone string, census domain.Fact[policy.CurrentConstruction]) ([]policy.ShelfRecord, int, error) {
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
func (r *RoutineStorageShelvesPlanner) build(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, need policy.ConcernID, reading observation.RoutineReading, step policy.ShelfStep, index int) (RoutineStorageShelvesResult, error) {
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
	building := &RoutineBuildingPlanner{reviewer: r.reviewer, native: r.native, goal: need, definition: policy.ShelfDefinition}
	for _, piece := range step.Pieces {
		if err := check(); err != nil {
			return RoutineStorageShelvesResult{}, err
		}
		value, err := domain.NewBuilding(piece.Def, piece.Anchor(), piece.Rot, stuff)
		if err != nil {
			return RoutineStorageShelvesResult{}, err
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
				return RoutineStorageShelvesResult{Verdict: rockNotDug(policy.ShelfDefinition, "no_open_cell_beside_footprint")}, nil
			}
			method := shelfMethod(step.Zone.Zone, index)
			dug, handled, err := building.admitRockStep(call, epoch, excavationStep{state: state, review: review, goal: goal, facts: facts, read: reading.ColonyReading}, planned, access, method, []domain.Building{value}, check)
			if err != nil {
				return RoutineStorageShelvesResult{}, err
			}
			if handled {
				out := RoutineStorageShelvesResult{Verdict: dug.Verdict}
				for _, m := range dug.Decision.Goal.Methods {
					if m.Method == method {
						out.Plan = m.Plan
					}
				}
				return out, nil
			}
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), value)
		if err != nil {
			return RoutineStorageShelvesResult{}, err
		}
		preview, _, err := r.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoutineStorageShelvesResult{}, err
		}
		v := preview.Preview
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !ck || !can || !sk || !safe {
			clockSchedulerLog("%s: shelf %s refused in zone %s", goal.OwnerID(), piece.Slot, step.Zone.Zone)
			continue
		}
		stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
		if err := mergeRoutineStock(&stock, preview.Stock, true); err != nil {
			return RoutineStorageShelvesResult{}, err
		}
		method := shelfMethod(step.Zone.Zone, index)
		result, err := building.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, method: method, reason: "shelf for zone " + step.Zone.Zone, snapshot: snapshot, selected: []policy.Preview{v}, stock: stock, purpose: policy.Routine})
		if err != nil || result.Verdict != BuildingReasonAdmitted {
			return RoutineStorageShelvesResult{Verdict: result.Verdict}, err
		}
		return RoutineStorageShelvesResult{Verdict: BuildingReasonAdmitted, Plan: snapshot.Plan}, nil
	}
	return RoutineStorageShelvesResult{Verdict: noSpace("storage_shelf")}, nil
}
