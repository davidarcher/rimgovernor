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
)

// RoutineRawFoodStockPlanner zones the raw cooking ingredients into the
// planned freezer beside the kitchen (#722): a 2x2 Critical stockpile of
// raw meat and raw plant food on the free patch nearest the freezer's door
// into the kitchen, the wall the stoves stand on (policy/layout_kitchen.go),
// so cooks fetch cold ingredients a step from the stove. Critical, like the
// meal shelf (#872): vanilla ranks Preferred below Important, and only a
// rank above the starter Important food zone hauls raw food into the cold. It is a method
// under MaintainRefrigeration, the goal that shells and cools the freezer.
// One stock per freezer, retried only after an earlier attempt failed.
type RoutineRawFoodStockPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineRawFoodStockSource
}

// RoutineRawFoodStockSource is the room census plus the zone preview.
type RoutineRawFoodStockSource interface {
	observation.RoutineSource
	FieldNative
}
type RoutineRawFoodStockResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineRawFoodStockPlanner(reviewer *RoutineReviewer, native RoutineRawFoodStockSource) (*RoutineRawFoodStockPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineRawFoodStockPlanner{reviewer: reviewer, native: native}, nil
}

func (r *RoutineRawFoodStockPlanner) Step(ctx context.Context) (RoutineRawFoodStockResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func (r *RoutineRawFoodStockPlanner) step(call, epoch context.Context) (RoutineRawFoodStockResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineRawFoodStockResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineRawFoodStockResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineRawFoodStockResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainRefrigeration {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineRawFoodStockResult{Reason: BuildingMethodNoDeficit}, nil
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineRawFoodStockResult{}, ErrControl
	}
	read, err := r.reviewer.observeRooms(call, r.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	projection := read.Projection
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return RoutineRawFoodStockResult{Reason: BuildingMethodUnknown}, nil
	}
	layout, rooms, known := plannedLayout(projection)
	if !known {
		return RoutineRawFoodStockResult{Reason: BuildingMethodUnknown}, nil
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	room, sites, err := rawFoodStockSites(layout, rooms, projection.Bounds, projection.Cells, layoutProtected(projection, protected))
	if err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	if room == "" {
		return RoutineRawFoodStockResult{Reason: BuildingMethodNoDeficit}, nil
	}
	var id domain.PlanID
	var method domain.MethodID
	for attempt := 0; attempt < maxIngredientStorageAttempts; attempt++ {
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/raw-food-stock/%s/%d", goal.Goal.ID, room, attempt)))
		candidate := domain.PlanID(fmt.Sprintf("routine-raw-food-stock-%x", digest[:16]))
		plan, err := p.journal.LoadPlan(call, candidate)
		if errors.Is(err, store.ErrNotFound) {
			id, method = candidate, domain.MethodID(fmt.Sprintf("raw-food-stock-%x-%d", digest[:8], attempt))
			break
		}
		if err != nil {
			return RoutineRawFoodStockResult{}, err
		}
		if !ingredientStorageFailed(plan.Progress) {
			return RoutineRawFoodStockResult{Reason: BuildingMethodUsed}, nil
		}
	}
	if id == "" {
		return RoutineRawFoodStockResult{Reason: BuildingMethodUsed}, nil
	}
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineRawFoodStockResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineRawFoodStockResult{}, err
	}
	if len(sites) == 0 {
		return RoutineRawFoodStockResult{Reason: BuildingMethodNoSpace}, nil
	}
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	var action domain.Action
	var evaluated policy.Preview
	accepted := false
	for _, candidate := range sites {
		value, err := domain.NewFilteredStockpileZone(rawFoodFilter, domain.CriticalPriority, candidate)
		if err != nil {
			return RoutineRawFoodStockResult{}, err
		}
		if action, err = domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value); err != nil {
			return RoutineRawFoodStockResult{}, err
		}
		preview, _, err := r.native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
		if err != nil {
			return RoutineRawFoodStockResult{}, err
		}
		v := preview.GetEvaluated()
		if v == nil {
			return RoutineRawFoodStockResult{}, ErrControl
		}
		if !v.GetAccepted() {
			continue
		}
		if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
			return RoutineRawFoodStockResult{}, ErrControl
		}
		evaluated = policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(candidate), Costs: domain.Known([]policy.Amount{})}
		accepted = true
		break
	}
	if !accepted {
		return RoutineRawFoodStockResult{Reason: BuildingMethodRefused}, nil
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	if p.session.State() != state {
		return RoutineRawFoodStockResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineRawFoodStockResult{}, observation.ErrStale
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Previews: []policy.Preview{evaluated}, Purpose: policy.Routine})
	if err != nil {
		return RoutineRawFoodStockResult{}, err
	}
	if !decision.Admitted {
		return RoutineRawFoodStockResult{Reason: BuildingMethodRefused}, nil
	}
	return RoutineRawFoodStockResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// rawFoodStockSites finds the first planned freezer standing in the census
// and lists the 2x2 patches inside it nearest its door into the kitchen (the
// Link; the outer Door on plans saved before #819). An empty room means no
// planned freezer stands yet.
func rawFoodStockSites(layout policy.LayoutPlan, rooms policy.RoomObservation, bounds policy.Bounds, cells []policy.SiteCell, protected []domain.Cell) (string, [][]domain.Cell, error) {
	for _, planned := range layout.Rooms {
		if planned.Role != policy.ModuleFreezer {
			continue
		}
		room, ok := policy.PlannedRoomStanding(planned, rooms)
		if !ok || len(room.Cells) == 0 {
			continue
		}
		anchor := planned.Door
		if planned.Link != nil {
			anchor = *planned.Link
		}
		sites, err := roomStorageSites(room.Cells, anchor, bounds, cells, protected)
		return room.ID, sites, err
	}
	return "", nil, nil
}

func centroid(cells []domain.Cell) domain.Cell {
	var sumX, sumZ int64
	for _, cell := range cells {
		sumX += int64(cell.X)
		sumZ += int64(cell.Z)
	}
	return domain.Cell{X: int32(sumX / int64(len(cells))), Z: int32(sumZ / int64(len(cells)))}
}

// roomStorageSites is the bounded list of free roofed 2x2 patches inside
// room, nearest anchor first; nil when nothing fits.
func roomStorageSites(room []domain.Cell, anchor domain.Cell, bounds policy.Bounds, cells []policy.SiteCell, protected []domain.Cell) ([][]domain.Cell, error) {
	inside := make(map[domain.Cell]bool, len(room))
	for _, cell := range room {
		inside[cell] = true
	}
	var scoped []policy.SiteCell
	for _, cell := range cells {
		if inside[cell.Cell] {
			scoped = append(scoped, cell)
		}
	}
	if len(scoped) == 0 {
		return nil, nil
	}
	sites, err := policy.CoveredStorageSites(policy.CoveredStorageRequest{Bounds: bounds, Anchor: anchor, Cells: scoped, Protected: protected})
	if err != nil || len(sites) == 0 {
		return nil, err
	}
	out := make([][]domain.Cell, 0, len(sites))
	for _, site := range sites {
		var block []domain.Cell
		for x := site.X; x < site.X+site.Width; x++ {
			for z := site.Z; z < site.Z+site.Height; z++ {
				block = append(block, domain.Cell{X: x, Z: z})
			}
		}
		out = append(out, block)
	}
	return out, nil
}

// ingredientStorageFailed reports a zone plan whose every action ended
// unsuccessful or cancelled, the only outcome that earns another attempt.

// rawFoodFilter is raw meat and raw plant food, never rotten.
var rawFoodFilter = func() domain.StockpileFilter {
	f, err := domain.NewStockpileFilter(domain.BaseNothing, []domain.FilterSelector{domain.CategoryDef("MeatRaw"), domain.CategoryDef("PlantFoodRaw")}, []domain.FilterSelector{domain.SpecialFilter("AllowRotten")})
	if err != nil {
		panic(err)
	}
	return f
}()
