package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineStorageShelvesPlanner (#721) places shelves inside the stockpiles
// this colony created -- the general store and the working stockpiles at the
// benches. Each shelf is a method of the goal that created its zone, taken
// only while that goal is active and selected; policy.NextShelfStep picks
// the step (one open shelf at a time, a third of the footprint at most).
// MaintainStockpiles configures every built shelf like its zone
// (policy.StockpileShelfPatch, role shelf:<buildingID>).
type RoutineStorageShelvesPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineStorageShelvesSource
}

// RoutineStorageShelvesSource is the room census and the building preview.
type RoutineStorageShelvesSource interface {
	RoutineBuildingSource
}

type RoutineStorageShelvesResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

// shelfGoals are the goals whose stockpiles get shelves: SecureSupplies'
// general store and MaintainResource's working stockpiles at the benches.
// Food storage (meal shelves, freezers) is planned by its own goals.
var shelfGoals = map[policy.GoalID]bool{policy.SecureSupplies: true, policy.MaintainResource: true}

// maxShelvesPerZone bounds the shelves a zone may ever be given.
const maxShelvesPerZone = 8

func NewRoutineStorageShelvesPlanner(reviewer *RoutineReviewer, native RoutineStorageShelvesSource) (*RoutineStorageShelvesPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	return &RoutineStorageShelvesPlanner{reviewer: reviewer, native: native}, nil
}

// shelfPlanID is world-scoped and epoch-free, so a shelf placed under one
// goal episode is still found in the next.
func shelfPlanID(s domain.GenerationSnapshot, zone string, index int) domain.PlanID {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d/%s/%d", s.Colony, s.Load, s.Map, zone, index)))
	return domain.PlanID(fmt.Sprintf("routine-storage-shelf-%x", digest[:16]))
}

func (r *RoutineStorageShelvesPlanner) step(call, epoch context.Context) (RoutineStorageShelvesResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineStorageShelvesResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineStorageShelvesResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineStorageShelvesResult{Reason: BuildingMethodNoReview}, nil
	}
	selected := map[domain.GoalID]policy.GoalID{}
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
		return RoutineStorageShelvesResult{Reason: BuildingMethodUnknown}, nil
	}
	goals := map[domain.GoalID]store.GoalState{}
	var zones []store.OwnedZone
	for _, z := range owned {
		if z.Kind != domain.StockpileZone || selected[z.Goal] == "" || z.Priority == domain.LowPriority {
			continue
		}
		if _, loaded := goals[z.Goal]; !loaded {
			g, err := p.journal.LoadGoal(call, z.Goal)
			if err != nil {
				return RoutineStorageShelvesResult{}, err
			}
			goals[z.Goal] = g
		}
		if g := goals[z.Goal].Goal; g.Status == domain.GoalActive && g.Need == domain.NeedDeficit {
			zones = append(zones, z)
		}
	}
	if len(zones) == 0 {
		return RoutineStorageShelvesResult{Reason: BuildingMethodNoDeficit}, nil
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineStorageShelvesResult{}, ErrControl
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
		shelves, index, err := zoneShelves(call, p.journal, state.Snapshot, z.ID, facts.Facts.CurrentConstruction)
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
			return RoutineStorageShelvesResult{Reason: BuildingMethodExhausted}, nil
		}
		return r.build(call, epoch, state, review, goals[z.Goal], selected[z.Goal], reading, step, next[z.ID])
	}
	return RoutineStorageShelvesResult{Reason: BuildingMethodUsed}, nil
}

// zoneShelves reads back the zone's shelf plans in index order: the
// shelves built or in flight, and the next free index.
func zoneShelves(ctx context.Context, journal *store.Store, s domain.GenerationSnapshot, zone string, census domain.Fact[policy.CurrentConstruction]) ([]policy.ShelfRecord, int, error) {
	var out []policy.ShelfRecord
	for index := 0; index < maxShelvesPerZone; index++ {
		plan, err := journal.LoadPlan(ctx, shelfPlanID(s, zone, index))
		if errors.Is(err, store.ErrNotFound) {
			return out, index, nil
		}
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
		built, _ := policy.BuiltActions(census)
		for _, progress := range plan.Progress {
			if id, ok := built[progress.View().Action]; ok {
				record.Building = id
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
func (r *RoutineStorageShelvesPlanner) build(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, need policy.GoalID, reading observation.RoutineReading, step policy.ShelfStep, index int) (RoutineStorageShelvesResult, error) {
	p := r.reviewer.player
	facts := reading.Projection
	stuff := ""
	for _, d := range facts.Definitions {
		if d.Name == policy.ShelfDefinition {
			stuff, _ = d.Stuff.Value()
		}
	}
	snapshot := state.Snapshot
	snapshot.Plan = shelfPlanID(state.Snapshot, step.Zone.Zone, index)
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return ErrControl
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
			clockSchedulerLog("%s: shelf %s refused in zone %s", goal.Goal.ID, piece.Slot, step.Zone.Zone)
			continue
		}
		stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
		if err := mergeRoutineStock(&stock, preview.Stock, true); err != nil {
			return RoutineStorageShelvesResult{}, err
		}
		method := domain.MethodID(fmt.Sprintf("storage-shelf-%s-%d", step.Zone.Zone, index))
		result, err := building.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, method: method, reason: "shelf for zone " + step.Zone.Zone, snapshot: snapshot, selected: []policy.Preview{v}, stock: stock, purpose: policy.Routine})
		if err != nil || result.Reason != BuildingMethodAdmitted {
			return RoutineStorageShelvesResult{Reason: result.Reason}, err
		}
		return RoutineStorageShelvesResult{Reason: BuildingMethodAdmitted, Plan: snapshot.Plan}, nil
	}
	return RoutineStorageShelvesResult{Reason: BuildingMethodNoSpace}, nil
}
