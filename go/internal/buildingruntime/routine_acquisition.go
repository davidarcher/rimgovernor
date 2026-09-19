package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RoutineAcquisitionPlanner struct {
	reviewer *RoutineReviewer
	need     policy.GoalID
}

type RoutineAcquisitionResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

// NewRoutineAcquisitionPlanner plans one acquisition goal: EnsureFoodSupply
// and MaintainWood harvest and hunt toward a stock target; ClearPests
// (#247) hunts every recognised pest the wild-animal census reports, one
// hunt method per admission, until none remain.
func NewRoutineAcquisitionPlanner(reviewer *RoutineReviewer, need policy.GoalID) (*RoutineAcquisitionPlanner, error) {
	if reviewer == nil || (need != policy.MaintainWood && need != policy.EnsureFoodSupply && need != policy.ClearPests) {
		return nil, ErrControl
	}
	return &RoutineAcquisitionPlanner{reviewer: reviewer, need: need}, nil
}
func (r *RoutineAcquisitionPlanner) Step(ctx context.Context) (RoutineAcquisitionResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineAcquisitionPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineAcquisitionResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineAcquisitionResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineAcquisitionResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineAcquisitionResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == r.need {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			break
		}
	}
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineAcquisitionResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if goal.Goal.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == r.need && row.Selected
		}
		if !selected {
			return RoutineAcquisitionResult{Reason: BuildingMethodRefused}, nil
		}
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	definitions := routineProjectDefinitions(plans, state.Snapshot, playerPlans)
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineAcquisitionResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	projection := read.Projection
	huntSources := map[string]bool{}
	if rows, known := projection.Acquisition.Value(); known {
		for _, row := range rows {
			if row.Hunt {
				huntSources[row.ID] = true
			}
		}
	}
	pest := r.need == policy.ClearPests
	food := r.need == policy.EnsureFoodSupply
	huntOnly := false
	pests := policy.PestCensus(projection.Facts.AnimalUpkeep.WildAnimals)
	reloadPlans := false
	stalledSources := map[string]bool{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		if pest {
			// An unissued pest hunt follows its animal wherever the census
			// reports it; one whose animal died to the defense or left the
			// map would be held forever and the goal behind it, so it is
			// cancelled at once.
			for _, action := range gonePestHunts(plan.Progress, projection.Facts.AnimalUpkeep.WildAnimals) {
				if _, err = p.journal.Cancel(call, method.Plan, action); err != nil {
					return RoutineAcquisitionResult{}, err
				}
				reloadPlans = true
			}
		}
		// A pest hunt follows its animal (#321) and native settles it on
		// the animal's death or departure, so the hunt-stall rule is not
		// its exit: the pest goal has no other prey to try for that animal,
		// and cancelling the hunt withdraws the designation from under its
		// hunter only to re-plan the same animal (#455).
		if !pest {
			for _, stalled := range stalledHuntActions(plan.Progress, huntSources, expected.Tick, r.reviewer.policy.HuntStallTicks) {
				// HuntingSafety.RouteSafe (native) stays authoritative and is never
				// bypassed here -- this only stops RimGovernor's own planner from
				// staying wedged behind an action native keeps correctly refusing
				// to let through, freeing it to try a different prey or source.
				if _, err = p.journal.Cancel(call, method.Plan, stalled); err != nil {
					return RoutineAcquisitionResult{}, err
				}
				reloadPlans = true
			}
		}
		stalled, err := stalledAcquisitionDesignations(call, p.journal, plan.Progress, huntSources, expected.Tick, r.reviewer.policy.AcquisitionStallTicks)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		for _, v := range stalled {
			if _, err = p.journal.Cancel(call, method.Plan, v.Action); err != nil {
				return RoutineAcquisitionResult{}, err
			}
			stalledSources[v.Thing] = true
			reloadPlans = true
		}
		plan, err = p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		// A stock goal works one plan at a time. ClearPests hunts every
		// animal of the pack: a hunt already dispatched holds its own
		// animal (held, below) and its designation counts against the
		// two outstanding hunts (slots), but does not stop the next
		// animal being planned. One hunt awaiting its kill blocked the
		// other beaver for a day (run 9 of #247).
		// EnsureFoodSupply's hunts are the exception (#260): a forage
		// batch harvests one bush at a time for days while the hunt rows
		// the butcher spot and bill were placed for wait behind it, so
		// open plant harvests leave the hunt slots plannable and the
		// hunt-only plan is admitted over them.
		if !pest && acquisitionBlockingWork(plan.Progress) {
			if !food || !acquisitionHuntOnlyOpen(plan.Progress, huntSources) {
				return RoutineAcquisitionResult{Reason: BuildingMethodExistingWork}, nil
			}
			huntOnly = true
		}
	}
	if reloadPlans {
		if plans, err = p.journal.LoadPlans(call, 256); err != nil {
			return RoutineAcquisitionResult{}, err
		}
	}
	pending := projection.PendingWoodUnits
	deficit := domain.Unknown[float64]()
	if food {
		pending = projection.PendingFoodNutrition
	}
	// A designation nobody took still counts in native's pending totals;
	// without this the re-plan would see its own stalled yield as covering
	// the deficit and propose nothing.
	if rows, known := projection.Acquisition.Value(); known && len(stalledSources) > 0 {
		if outstanding, pk := pending.Value(); pk {
			for _, row := range rows {
				if !stalledSources[row.ID] || !row.Designated {
					continue
				}
				if food {
					outstanding -= row.NutritionYield
				} else {
					outstanding -= row.Yield
				}
			}
			pending = domain.Known(max(0, outstanding))
		}
	}
	if food {
		plan, known := projection.Facts.FoodPlan.Value()
		if !known {
			return RoutineAcquisitionResult{Reason: BuildingMethodUnknown}, nil
		}
		projection.Acquisition, deficit = foodPlanAcquisition(plan, projection.Acquisition)
		clockSchedulerLog("Food acquisition: %s", plan.Explain())
	} else if wood, known := projection.Facts.Wood.Value(); known {
		deficit = domain.Known(max(0, float64(r.reviewer.seasonal(projection.Facts).WoodTarget)-float64(wood)))
	}
	held := map[string]bool{}
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			if acquisition, ok := progress.Action().Acquisition(); ok && domain.GoalWorkOpen([]domain.Progress{progress}) {
				held[acquisition.Thing()] = true
			}
		}
	}
	slots := domain.Unknown[int]()
	if n, known := projection.PendingHunts.Value(); known {
		slots = domain.Known(max(0, 2-n))
	}
	// A hunt needs a hunter: with the roster known and HunterFor (Shooting,
	// a ranged primary, never a Brawler) finding nobody, the hunting budget
	// is zero and only gathering is proposed, instead of a designation
	// native's hunt preview would refuse for want of a free ranged hunter.
	if pawns, known := projection.WorkPawns.Value(); known {
		if _, ok := policy.HunterFor(policy.Profiles(pawns)); !ok {
			slots = domain.Known(0)
		}
	}
	var selected []policy.AcquisitionSource
	if pest {
		selected, err = policy.SelectPestAcquisition(projection.Acquisition, pests, held, slots)
	} else {
		sources := projection.Acquisition
		if huntOnly {
			sources = huntRows(sources)
		}
		selected, err = policy.SelectAcquisition(sources, deficit, pending, food, held, slots)
	}
	if rows, known := projection.Acquisition.Value(); known && !pest {
		hunts := 0
		for _, row := range rows {
			if row.Hunt {
				hunts++
			}
		}
		clockSchedulerLog("%s: acquisition select rows=%d hunts=%d deficit=%v pending=%v slots=%v held=%d selected=%d err=%v", goal.Goal.ID, len(rows), hunts, deficit, pending, slots, len(held), len(selected), err)
	}
	if err != nil {
		return RoutineAcquisitionResult{Reason: BuildingMethodUnknown}, nil
	}
	if len(selected) == 0 {
		return RoutineAcquisitionResult{Reason: BuildingMethodUsed}, nil
	}
	hash := sha256.New()
	for _, row := range selected {
		fmt.Fprintf(hash, "%s/%s/%s/%d/%d\n", row.ID, row.Resource, row.Token, row.Cell.X, row.Cell.Z)
	}
	prefix, planPrefix := "acquire", "routine-acquire"
	if pest {
		// Every admission the goal ever made salts the pest method: a
		// cancelled hunt of an animal that came back to the same cell in
		// the same state rehashes to a fresh method instead of reading as
		// already used (#214).
		fmt.Fprintf(hash, "#%d\n", goal.Admitted)
		prefix, planPrefix = "pest-hunt", "routine-pest-hunt"
	}
	method := domain.MethodID(fmt.Sprintf("%s-%x", prefix, hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineAcquisitionResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineAcquisitionResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("%s-%x", planPrefix, digest[:16]))
	var actions []domain.Action
	for i, row := range selected {
		value, err := domain.NewAcquisition(row.ID, row.Resource, row.Cell)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		action, err := domain.NewAcquisitionAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if p.session.State() != state {
		return RoutineAcquisitionResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineAcquisitionResult{}, err
	}
	return RoutineAcquisitionResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// Only admitted sources can start new work. Pending designations retain their
// native credit in SelectAcquisition; the ledger never calls them delivered.
func foodPlanAcquisition(plan policy.FoodPlan, sources domain.Fact[[]policy.AcquisitionSource]) (domain.Fact[[]policy.AcquisitionSource], domain.Fact[float64]) {
	rows, known := sources.Value()
	if !known {
		return domain.Unknown[[]policy.AcquisitionSource](), domain.Unknown[float64]()
	}
	open := map[string]bool{}
	for _, entry := range plan.Portfolio {
		if (entry.Channel.Kind == policy.FoodForage || entry.Channel.Kind == policy.FoodHunt) && entry.Decision == policy.FoodPlanOpen {
			open[entry.Channel.ID] = true
		}
	}
	selected, nutrition := []policy.AcquisitionSource{}, 0.0
	for _, row := range rows {
		if open[row.ID] {
			selected = append(selected, row)
			nutrition += row.NutritionYield
		}
	}
	return domain.Known(selected), domain.Known(nutrition)
}

// gonePestHunts finds the unissued (pending or prepared) hunt actions of a
// pest plan whose animal the wild-animal census no longer lists (dead or
// off the map). The census has to be known: an unknown census is not
// evidence the pack is gone. A pest that is merely ineligible this step
// (no free hunter) or has wandered off its planned cell keeps its row in
// the wild census and its hunt, which follows it (#321).
func gonePestHunts(progress []domain.Progress, wild domain.Fact[[]policy.UpkeepAnimal]) (gone []domain.ActionID) {
	animals, known := wild.Value()
	if !known {
		return nil
	}
	alive := map[string]bool{}
	for _, animal := range animals {
		alive[string(animal.ID)] = true
	}
	for _, p := range progress {
		acquisition, ok := p.Action().Acquisition()
		v := p.View()
		if ok && (v.Stage == domain.Pending || v.Stage == domain.Prepared) && !alive[acquisition.Thing()] {
			gone = append(gone, v.Action)
		}
	}
	return gone
}

// A queued production bill may be waiting for ingredients acquired by this method.
// acquisitionHuntOnlyOpen reports open acquisition work made only of
// non-hunt harvests: nothing else of the plan is open and no hunt is.
func acquisitionHuntOnlyOpen(progress []domain.Progress, huntSources map[string]bool) bool {
	open := false
	for _, p := range progress {
		if !domain.GoalWorkOpen([]domain.Progress{p}) {
			continue
		}
		acquisition, ok := p.Action().Acquisition()
		if !ok || huntSources[acquisition.Thing()] {
			return false
		}
		open = true
	}
	return open
}

// huntRows keeps the census's hunt rows.
func huntRows(sources domain.Fact[[]policy.AcquisitionSource]) domain.Fact[[]policy.AcquisitionSource] {
	rows, known := sources.Value()
	if !known {
		return sources
	}
	var hunts []policy.AcquisitionSource
	for _, row := range rows {
		if row.Hunt {
			hunts = append(hunts, row)
		}
	}
	return domain.Known(hunts)
}

func acquisitionBlockingWork(progress []domain.Progress) bool {
	for _, p := range progress {
		if p.Action().Kind() != domain.ProductionBillAction && domain.GoalWorkOpen([]domain.Progress{p}) {
			return true
		}
	}
	return false
}

// stalledHuntActions finds dispatched Hunt-kind acquisition actions that have
// stayed unresolved for at least graceTicks. Native's HuntingSafety.RouteSafe
// can repeatedly interrupt the shared game clock while a hunter's route stays
// unsafe, which leaves the dispatched action's evidence unresolved -- it never
// completes, fails, or gets re-inspected -- so it reads as open work forever
// and blocks acquisitionBlockingWork's caller from proposing anything else.
// graceTicks <= 0 disables this (never treats anything as stalled). Pest
// hunts are not subject to it (#321, #455).
// stalledDesignation is a dispatched, still-designated harvest nobody has
// taken: the action and the source thing the planner must stop counting.
type stalledDesignation struct {
	Action domain.ActionID
	Thing  string
}

// stalledAcquisitionDesignations finds dispatched non-hunt acquisition
// actions whose effect has stayed pending (designated, no labor) for at
// least stallTicks since their dispatch (#291): native reports why in the
// pending effect's reason (worker outcome detail), and the planner cancels
// them so the goal re-plans from another source instead of holding a
// method (and a development slot) on one plant nobody harvests. The native
// executor then withdraws the designation natively (CancelAcquisition) and
// the withdrawn record's terminal effect settles the action; an already
// cancelled action is left to that. stallTicks <= 0 disables this.
func stalledAcquisitionDesignations(ctx context.Context, journal *store.Store, progress []domain.Progress, huntSources map[string]bool, now domain.Tick, stallTicks int64) ([]stalledDesignation, error) {
	if stallTicks <= 0 {
		return nil, nil
	}
	var stalled []stalledDesignation
	for _, p := range progress {
		acquisition, ok := p.Action().Acquisition()
		v := p.View()
		effect, known := v.Effect.Value()
		if !ok || huntSources[acquisition.Thing()] || !v.Unresolved || v.Stage == domain.Cancelled || !known || effect != domain.EffectPending {
			continue
		}
		dispatched, err := journal.DispatchTick(ctx, v.Action)
		if err != nil {
			return nil, err
		}
		if since, known := dispatched.Value(); known && int64(now-since) >= stallTicks {
			stalled = append(stalled, stalledDesignation{v.Action, acquisition.Thing()})
		}
	}
	return stalled, nil
}

func stalledHuntActions(progress []domain.Progress, huntSources map[string]bool, now domain.Tick, graceTicks int64) []domain.ActionID {
	if graceTicks <= 0 {
		return nil
	}
	var stalled []domain.ActionID
	for _, p := range progress {
		acquisition, ok := p.Action().Acquisition()
		v := p.View()
		if ok && huntSources[acquisition.Thing()] && v.Unresolved && v.Stage != domain.Cancelled && int64(now-v.Tick) >= graceTicks {
			stalled = append(stalled, v.Action)
		}
	}
	return stalled
}
