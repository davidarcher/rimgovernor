package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineIngredientStoragePlanner is the ingredient-storage rung of the
// production ladder (issue #4 M4): once a bench hosts an available recipe for
// a MaintainResource deficit, it places one allow-listed stockpile for that
// recipe's ingredients on the nearest free roofed 2x2 patch inside the room
// the native census reports as the Workshop, so hauling brings the inputs to
// the bench instead of leaving them wherever they dropped. The bill itself
// stays with RoutineResourcePlanner; the zone is a second method under the
// same goal and never waits on the bill's open work.
type RoutineIngredientStoragePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineIngredientStorageSource
}

// RoutineIngredientStorageSource is the workshop's bench census plus the zone
// preview the stockpile needs.
type RoutineIngredientStorageSource interface {
	RoutineWorkshopSource
	FieldNative
}
type RoutineIngredientStorageResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

const maxIngredientStorageAttempts = 3

func NewRoutineIngredientStoragePlanner(reviewer *RoutineReviewer, native RoutineIngredientStorageSource) (*RoutineIngredientStoragePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	return &RoutineIngredientStoragePlanner{reviewer: reviewer, native: native}, nil
}

func (r *RoutineIngredientStoragePlanner) Step(ctx context.Context) (RoutineIngredientStorageResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func (r *RoutineIngredientStoragePlanner) step(call, epoch context.Context) (RoutineIngredientStorageResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineIngredientStorageResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineIngredientStorageResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineIngredientStorageResult{Reason: BuildingMethodNoReview}, nil
	}
	if !r.reviewer.policy.ResourceGoalConfigured() && review.MedicineTarget == 0 {
		return RoutineIngredientStorageResult{Reason: BuildingMethodDisabled}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainResource {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineIngredientStorageResult{Reason: BuildingMethodNoDeficit}, nil
	}
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainResource && row.Selected
	}
	if !selected {
		return RoutineIngredientStorageResult{Reason: BuildingMethodRefused}, nil
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil || bridge.ValidateColonyFacts(observed, identity) != nil {
		return RoutineIngredientStorageResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineIngredientStorageResult{}, ErrControl
	}
	stock := resourceStockFacts(observed)
	targets, err := r.reviewer.resourceTargets(call, state.Snapshot, stock)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	resource, _, ok, err := policy.SelectResourceTarget(targets, stock)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	if !ok {
		return RoutineIngredientStorageResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if resource == "Beer" {
		resource = "Wort"
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	if len(census) > 256 {
		return RoutineIngredientStorageResult{}, ErrControl
	}
	benches := make([]policy.GearBench, 0, len(census))
	for _, row := range census {
		benches = append(benches, row.Bench)
	}
	bench, recipe, allow, known := ingredientStorageAllowList(resource, benches)
	if !known {
		return RoutineIngredientStorageResult{Reason: BuildingMethodUnknown}, nil
	}
	if recipe == "" {
		// No standing bench hosts the recipe yet: the workshop rung owns the
		// deficit until one does.
		return RoutineIngredientStorageResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if len(allow) == 0 {
		return RoutineIngredientStorageResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// One zone per bench and recipe, retried only after an earlier attempt
	// ended unsuccessful or cancelled.
	var id domain.PlanID
	var method domain.MethodID
	for attempt := 0; attempt < maxIngredientStorageAttempts; attempt++ {
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%s/%d", goal.Goal.ID, bench, recipe, attempt)))
		candidate := domain.PlanID(fmt.Sprintf("routine-ingredient-storage-%x", digest[:16]))
		plan, err := p.journal.LoadPlan(call, candidate)
		if errors.Is(err, store.ErrNotFound) {
			id, method = candidate, domain.MethodID(fmt.Sprintf("ingredient-storage-%x-%d", digest[:8], attempt))
			break
		}
		if err != nil {
			return RoutineIngredientStorageResult{}, err
		}
		if !ingredientStorageFailed(plan.Progress) {
			return RoutineIngredientStorageResult{Reason: BuildingMethodUsed}, nil
		}
	}
	if id == "" {
		return RoutineIngredientStorageResult{Reason: BuildingMethodUsed}, nil
	}
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineIngredientStorageResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineIngredientStorageResult{}, err
	}
	last, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	expected, err := observation.DecodeIdentity(last)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineIngredientStorageResult{}, ErrControl
	}
	read, err := r.reviewer.observeRooms(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	projection := read.Projection
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return RoutineIngredientStorageResult{Reason: BuildingMethodUnknown}, nil
	}
	rooms, known := projection.Rooms.Value()
	if !known {
		return RoutineIngredientStorageResult{Reason: BuildingMethodUnknown}, nil
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	sites, err := ingredientStorageSites(rooms.Rooms, projection.Bounds, projection.Cells, protected)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	if len(sites) == 0 {
		return RoutineIngredientStorageResult{Reason: BuildingMethodNoSpace}, nil
	}
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	// A freshly built shell's floor carries what the census cannot report
	// (a pawn, a stack dropped after the read), so each candidate is
	// previewed in turn and a refused site gives way to the next (#223).
	var cells []domain.Cell
	var action domain.Action
	var evaluated policy.Preview
	accepted := false
	for _, candidate := range sites {
		value, err := domain.NewAllowListStockpileZone(domain.ImportantPriority, allow, candidate)
		if err != nil {
			return RoutineIngredientStorageResult{}, err
		}
		if action, err = domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value); err != nil {
			return RoutineIngredientStorageResult{}, err
		}
		preview, _, err := r.native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
		if err != nil {
			return RoutineIngredientStorageResult{}, err
		}
		v := preview.GetEvaluated()
		if v == nil {
			return RoutineIngredientStorageResult{}, ErrControl
		}
		if !v.GetAccepted() {
			continue
		}
		if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
			return RoutineIngredientStorageResult{}, ErrControl
		}
		cells = candidate
		evaluated = policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
		accepted = true
		break
	}
	if !accepted {
		return RoutineIngredientStorageResult{Reason: BuildingMethodRefused}, nil
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	if p.session.State() != state {
		return RoutineIngredientStorageResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineIngredientStorageResult{}, observation.ErrStale
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Rules: r.reviewer.rules, Previews: []policy.Preview{evaluated}, Purpose: policy.Routine})
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	if !decision.Admitted {
		return RoutineIngredientStorageResult{Reason: BuildingMethodRefused}, nil
	}
	return RoutineIngredientStorageResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// ingredientStorageAllowList picks the first bench (census order) hosting an
// available recipe for the resource and returns the sorted union of every
// definition any of that recipe's ingredient alternatives accepts. An empty
// recipe means no standing bench hosts one; known false means a fact the
// choice depends on is unobserved.
func ingredientStorageAllowList(resource policy.Resource, benches []policy.GearBench) (bench, recipe string, allow []string, known bool) {
	for _, b := range benches {
		recipes, rk := b.Recipes.Value()
		if !rk {
			return "", "", nil, false
		}
		for _, r := range recipes {
			if !containsProduct(r.Products, resource) {
				continue
			}
			available, ak := r.Available.Value()
			on, ok := r.AvailableOn.Value()
			if !ak || !ok {
				return "", "", nil, false
			}
			if !available || !on {
				continue
			}
			alternatives, ik := r.Ingredients.Value()
			if !ik {
				return "", "", nil, false
			}
			seen := map[string]bool{}
			for _, alternative := range alternatives {
				for _, amount := range alternative {
					if name := string(amount.Resource); name != "" && !seen[name] {
						seen[name] = true
						allow = append(allow, name)
					}
				}
			}
			sort.Strings(allow)
			return b.ID, r.Definition, allow, true
		}
	}
	return "", "", nil, true
}

func containsProduct(products []policy.Resource, resource policy.Resource) bool {
	for _, p := range products {
		if p == resource {
			return true
		}
	}
	return false
}

// ingredientStorageSites is the bounded list of free roofed 2x2 patches
// inside the first room the census reports in the Workshop role, nearest
// that room's centroid first. Nil when no room holds the role or nothing
// fits.
func ingredientStorageSites(rooms []policy.Room, bounds policy.Bounds, cells []policy.SiteCell, protected []domain.Cell) ([][]domain.Cell, error) {
	for _, room := range rooms {
		role, known := room.Role.Value()
		if !known || role != policy.RoomRoleWorkshop || len(room.Cells) == 0 {
			continue
		}
		inside := make(map[domain.Cell]bool, len(room.Cells))
		var sumX, sumZ int64
		for _, cell := range room.Cells {
			inside[cell] = true
			sumX += int64(cell.X)
			sumZ += int64(cell.Z)
		}
		anchor := domain.Cell{X: int32(sumX / int64(len(room.Cells))), Z: int32(sumZ / int64(len(room.Cells)))}
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
		if err != nil {
			return nil, err
		}
		if len(sites) == 0 {
			return nil, nil
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
	return nil, nil
}

// ingredientStorageFailed reports a zone plan whose every action ended
// unsuccessful or cancelled, the only outcome that earns another attempt.
func ingredientStorageFailed(progress []domain.Progress) bool {
	if len(progress) == 0 {
		return false
	}
	for _, p := range progress {
		stage := p.View().Stage
		if stage != domain.Unsuccessful && stage != domain.Cancelled {
			return false
		}
	}
	return true
}
