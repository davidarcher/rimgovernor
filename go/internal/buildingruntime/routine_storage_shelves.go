package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineStorageShelvesPlanner (#721) places shelves inside the stockpiles
// this colony created -- the general store and the working stockpiles at the
// benches -- and configures each built shelf like the zone it serves:
// the zone's filter and priority, claimed under role shelf:<buildingID>.
// Each shelf is a method of the goal that created its zone, taken only
// while that goal is active and selected; policy.NextShelfStep picks the
// step (patch before build, one open shelf at a time, a third of the
// footprint at most).
type RoutineStorageShelvesPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineStorageShelvesSource
}

// RoutineStorageShelvesSource is the room census, the building preview and
// the storage building read the shelf patch's CAS token comes from.
type RoutineStorageShelvesSource interface {
	RoutineBuildingSource
	ReadStorageBuildingTarget(context.Context, *c.Identity, string) (bridge.StorageBuildingTarget, bridge.Result, error)
}

type RoutineStorageShelvesResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

// shelfGoals are the goals whose stockpiles get shelves: SecureSupplies'
// general store and MaintainResource's working stockpiles at the benches.
// Food storage (meal shelves, freezers) is planned by its own goals.
var shelfGoals = map[policy.GoalID]bool{policy.SecureSupplies: true, policy.MaintainResource: true}

// Attempt bounds: shelves a zone may ever be given, and patches per shelf.
const (
	maxShelvesPerZone     = 8
	maxShelfPatchAttempts = 3
)

func NewRoutineStorageShelvesPlanner(reviewer *RoutineReviewer, native RoutineStorageShelvesSource) (*RoutineStorageShelvesPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	return &RoutineStorageShelvesPlanner{reviewer: reviewer, native: native}, nil
}

func (r *RoutineStorageShelvesPlanner) Step(ctx context.Context) (RoutineStorageShelvesResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

// shelfPlanID and shelfPatchPlanID are world-scoped and epoch-free, so a
// shelf placed under one goal episode is still found in the next.
func shelfPlanID(s domain.GenerationSnapshot, zone string, index int) domain.PlanID {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d/%s/%d", s.Colony, s.Load, s.Map, zone, index)))
	return domain.PlanID(fmt.Sprintf("routine-storage-shelf-%x", digest[:16]))
}

func shelfPatchPlanID(s domain.GenerationSnapshot, building string, attempt int) domain.PlanID {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d/%s/patch/%d", s.Colony, s.Load, s.Map, building, attempt)))
	return domain.PlanID(fmt.Sprintf("routine-storage-shelf-patch-%x", digest[:16]))
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
		shelves, index, err := r.zoneShelves(call, state.Snapshot, z.ID)
		if err != nil {
			return RoutineStorageShelvesResult{}, err
		}
		next[z.ID] = index
		request.Shelves = append(request.Shelves, shelves...)
	}
	step := policy.NextShelfStep(request)
	switch step.Kind {
	case policy.ShelfPatch:
		z := owner[step.Zone.Zone]
		return r.patch(call, epoch, state, goals[z.Goal], step, facts.Identity.Tick)
	case policy.ShelfBuild:
		z := owner[step.Zone.Zone]
		if next[z.ID] >= maxShelvesPerZone {
			return RoutineStorageShelvesResult{Reason: BuildingMethodExhausted}, nil
		}
		return r.build(call, epoch, state, review, goals[z.Goal], selected[z.Goal], reading, step, next[z.ID])
	}
	return RoutineStorageShelvesResult{Reason: BuildingMethodUsed}, nil
}

// zoneShelves reads back the zone's shelf plans in index order: the
// shelves built or in flight, their patch state, and the next free index.
func (r *RoutineStorageShelvesPlanner) zoneShelves(ctx context.Context, s domain.GenerationSnapshot, zone string) ([]policy.ShelfRecord, int, error) {
	journal := r.reviewer.player.journal
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
		if domain.GoalWorkOpen(plan.Progress) {
			record.Open = true
			out = append(out, record)
			continue
		}
		if !routineBuildingCompleted(plan.Progress) {
			continue
		}
		for _, progress := range plan.Progress {
			if built, ok := progress.View().Construction.Value(); ok {
				record.Building = built.Current
			}
		}
		if record.Building == "" {
			continue
		}
		for attempt := 0; attempt < maxShelfPatchAttempts; attempt++ {
			patch, err := journal.LoadPlan(ctx, shelfPatchPlanID(s, record.Building, attempt))
			if errors.Is(err, store.ErrNotFound) {
				break
			}
			if err != nil {
				return nil, 0, err
			}
			if domain.GoalWorkOpen(patch.Progress) {
				record.Open = true
				break
			}
			if routineBuildingCompleted(patch.Progress) {
				record.Patched = true
				break
			}
			if attempt == maxShelfPatchAttempts-1 {
				// Every attempt failed: leave the shelf as it stands.
				record.Patched = true
			}
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
		if v.Action != action || !v.Snapshot.Matches(snapshot) || !preview.Stock.Snapshot.Matches(snapshot) {
			return RoutineStorageShelvesResult{}, ErrControl
		}
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
		result, err := building.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, read: reading.ColonyReading, method: method, reason: "shelf for zone " + step.Zone.Zone, snapshot: snapshot, selected: []policy.Preview{v}, stock: stock, purpose: policy.Routine, check: check})
		if err != nil || result.Reason != BuildingMethodAdmitted {
			return RoutineStorageShelvesResult{Reason: result.Reason}, err
		}
		return RoutineStorageShelvesResult{Reason: BuildingMethodAdmitted, Plan: snapshot.Plan}, nil
	}
	return RoutineStorageShelvesResult{Reason: BuildingMethodNoSpace}, nil
}

// patch configures a built shelf like its zone under the shelf's current
// storage token.
func (r *RoutineStorageShelvesPlanner) patch(call, epoch context.Context, state ControlState, goal store.GoalState, step policy.ShelfStep, tick domain.Tick) (RoutineStorageShelvesResult, error) {
	p := r.reviewer.player
	target, _, err := r.native.ReadStorageBuildingTarget(call, boundary.Identity(state.Snapshot), step.Shelf.Building)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	if _, err = boundary.Context(target.Context, state.Snapshot); err != nil || domain.Tick(target.Context.GetTick()) < tick {
		return RoutineStorageShelvesResult{}, ErrControl
	}
	if !target.Present {
		return RoutineStorageShelvesResult{Reason: BuildingMethodUsed}, nil
	}
	attempt := 0
	var id domain.PlanID
	for ; attempt < maxShelfPatchAttempts; attempt++ {
		id = shelfPatchPlanID(state.Snapshot, step.Shelf.Building, attempt)
		if _, err := p.journal.LoadPlan(call, id); errors.Is(err, store.ErrNotFound) {
			break
		} else if err != nil {
			return RoutineStorageShelvesResult{}, err
		}
	}
	if attempt == maxShelfPatchAttempts {
		return RoutineStorageShelvesResult{Reason: BuildingMethodExhausted}, nil
	}
	value, err := domain.NewStockpilePatch(domain.StorageBuildingTarget, step.Shelf.Building, target.Token, step.Zone.Filter, step.Zone.Priority, policy.ShelfRole(step.Shelf.Building))
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	action, err := domain.NewStockpilePatchAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	if p.session.State() != state {
		return RoutineStorageShelvesResult{}, ErrControl
	}
	method := domain.MethodID(fmt.Sprintf("storage-shelf-patch-%s-%d", step.Shelf.Building, attempt))
	if _, err = p.journal.CommitGoalMethodReason(call, goal.Goal.ID, goal.Revision, method, "configure shelf like zone "+step.Zone.Zone, plan); err != nil {
		if errors.Is(err, store.ErrNotAdmitted) {
			return RoutineStorageShelvesResult{Reason: BuildingMethodRefused}, nil
		}
		return RoutineStorageShelvesResult{}, err
	}
	return RoutineStorageShelvesResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
