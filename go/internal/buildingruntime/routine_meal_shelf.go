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

// RoutineMealShelfPlanner zones a small Critical-priority meal shelf beside
// the dining table (#872): a 2x2 allow-listed stockpile for cooked meals on
// the free roofed patch nearest the table inside the census Dining room, so
// a hungry pawn eats at the table without walking to the storeroom. It is a
// method under EnsureComfort, the goal the dining table itself is built for,
// and stays off the table's adjacent cells, which the chairs take. One shelf
// per dining room, retried only after an earlier attempt failed.
type RoutineMealShelfPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineMealShelfSource
}

// RoutineMealShelfSource is the room census plus the zone preview.
type RoutineMealShelfSource interface {
	observation.RoutineSource
	FieldNative
}
type RoutineMealShelfResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

// mealShelfDefinitions are the cooked meals the shelf accepts.
var mealShelfDefinitions = []string{"MealFine", "MealLavish", "MealNutrientPaste", "MealSimple", "MealSurvivalPack"}

func NewRoutineMealShelfPlanner(reviewer *RoutineReviewer, native RoutineMealShelfSource) (*RoutineMealShelfPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineMealShelfPlanner{reviewer: reviewer, native: native}, nil
}

func (r *RoutineMealShelfPlanner) Step(ctx context.Context) (RoutineMealShelfResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineMealShelfResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func (r *RoutineMealShelfPlanner) step(call, epoch context.Context) (RoutineMealShelfResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineMealShelfResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineMealShelfResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineMealShelfResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineMealShelfResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureComfort {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineMealShelfResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineMealShelfResult{Reason: BuildingMethodNoDeficit}, nil
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineMealShelfResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineMealShelfResult{}, ErrControl
	}
	read, err := r.reviewer.observeRooms(call, r.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineMealShelfResult{}, err
	}
	projection := read.Projection
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return RoutineMealShelfResult{Reason: BuildingMethodUnknown}, nil
	}
	rooms, rk := projection.Rooms.Value()
	comfort, ck := projection.Facts.Comfort.Value()
	if !rk || !ck {
		return RoutineMealShelfResult{Reason: BuildingMethodUnknown}, nil
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineMealShelfResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	room, sites, err := mealShelfSites(rooms.Rooms, comfort.Surfaces, projection.Bounds, projection.Cells, layoutProtected(projection, protected))
	if err != nil {
		return RoutineMealShelfResult{}, err
	}
	if room == "" {
		return RoutineMealShelfResult{Reason: BuildingMethodNoDeficit}, nil
	}
	var id domain.PlanID
	var method domain.MethodID
	for attempt := 0; attempt < maxIngredientStorageAttempts; attempt++ {
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/meal-shelf/%s/%d", goal.Goal.ID, room, attempt)))
		candidate := domain.PlanID(fmt.Sprintf("routine-meal-shelf-%x", digest[:16]))
		plan, err := p.journal.LoadPlan(call, candidate)
		if errors.Is(err, store.ErrNotFound) {
			id, method = candidate, domain.MethodID(fmt.Sprintf("meal-shelf-%x-%d", digest[:8], attempt))
			break
		}
		if err != nil {
			return RoutineMealShelfResult{}, err
		}
		if !ingredientStorageFailed(plan.Progress) {
			return RoutineMealShelfResult{Reason: BuildingMethodUsed}, nil
		}
	}
	if id == "" {
		return RoutineMealShelfResult{Reason: BuildingMethodUsed}, nil
	}
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineMealShelfResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineMealShelfResult{}, err
	}
	if len(sites) == 0 {
		return RoutineMealShelfResult{Reason: BuildingMethodNoSpace}, nil
	}
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	var action domain.Action
	var evaluated policy.Preview
	accepted := false
	for _, candidate := range sites {
		value, err := allowListZone(domain.CriticalPriority, mealShelfDefinitions, candidate)
		if err != nil {
			return RoutineMealShelfResult{}, err
		}
		if action, err = domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value); err != nil {
			return RoutineMealShelfResult{}, err
		}
		preview, _, err := r.native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
		if err != nil {
			return RoutineMealShelfResult{}, err
		}
		v := preview.GetEvaluated()
		if v == nil {
			return RoutineMealShelfResult{}, ErrControl
		}
		if !v.GetAccepted() {
			continue
		}
		if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
			return RoutineMealShelfResult{}, ErrControl
		}
		evaluated = policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(candidate), Costs: domain.Known([]policy.Amount{})}
		accepted = true
		break
	}
	if !accepted {
		return RoutineMealShelfResult{Reason: BuildingMethodRefused}, nil
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineMealShelfResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineMealShelfResult{}, err
	}
	if p.session.State() != state {
		return RoutineMealShelfResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineMealShelfResult{}, observation.ErrStale
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Previews: []policy.Preview{evaluated}, Purpose: policy.Routine})
	if err != nil {
		return RoutineMealShelfResult{}, err
	}
	if !decision.Admitted {
		return RoutineMealShelfResult{Reason: BuildingMethodRefused}, nil
	}
	return RoutineMealShelfResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// mealShelfSites finds the first census Dining room holding a dining surface
// and lists the 2x2 patches inside it nearest that table, never on the
// table's adjacent cells (the chairs' places). An empty room means no dining
// table stands in a Dining room yet.
func mealShelfSites(rooms []policy.Room, surfaces []policy.DiningSurface, bounds policy.Bounds, cells []policy.SiteCell, protected []domain.Cell) (string, [][]domain.Cell, error) {
	for _, room := range rooms {
		role, known := room.Role.Value()
		if !known || role != policy.RoomRoleDiningRoom || len(room.Cells) == 0 {
			continue
		}
		for _, s := range surfaces {
			if s.RoomID != room.ID || len(s.Adjacent) == 0 {
				continue
			}
			sites, err := roomStorageSites(room.Cells, centroid(s.Adjacent), bounds, cells, append(append([]domain.Cell(nil), protected...), s.Adjacent...))
			return room.ID, sites, err
		}
	}
	return "", nil, nil
}
