package buildingruntime

import (
	"context"
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineHomeCoverageSource reads current buildings by exact claimed identity
// (to re-derive ownership fresh, the same evidence OwnedConstructions
// requires) and the general colony census carrying the Home coverage/upkeep
// section. No dedicated Home-coverage read exists; ReadColonyFacts's Upkeep
// facts are unconditional, unlike Planning, so planning is left false here.
type RoutineHomeCoverageSource interface {
	ReadConstructionBuildings(context.Context, *c.Identity, []string) (*o.ListBuildingsReply, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
}
type RoutineHomeCoveragePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineHomeCoverageSource
}
type RoutineHomeCoverageResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineHomeCoveragePlanner(reviewer *RoutineReviewer, native RoutineHomeCoverageSource) (*RoutineHomeCoveragePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineHomeCoveragePlanner{reviewer, native}, nil
}
func (r *RoutineHomeCoveragePlanner) Step(ctx context.Context) (RoutineHomeCoverageResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

// homeCoverageObservationFacts decodes the same unconditional Upkeep section
// observation.colonyHomeCoverage does for routine review. Duplicated here for
// the same reason gearObservationFacts is duplicated in routine_gear.go: a
// fresh census immediately before proposing a method, not the review's cache.
func homeCoverageObservationFacts(v *o.ColonyFactsSnapshot) (policy.HomeCoverageObservation, bool) {
	u := v.GetUpkeep().GetObserved()
	if u == nil {
		return policy.HomeCoverageObservation{}, false
	}
	h := u.GetHomeCoverage().GetObserved()
	if h == nil {
		return policy.HomeCoverageObservation{}, false
	}
	result := policy.HomeCoverageObservation{Revision: h.GetRevision(), Targets: []policy.HomeCoverageTarget{}}
	for _, row := range h.Targets {
		t := policy.HomeCoverageTarget{ID: row.GetId(), Blocker: row.GetBlocker(), Cells: []domain.Cell{}}
		if row.ShapeToken != nil {
			t.Shape = domain.Known(row.GetShapeToken())
		}
		if row.MissingCells != nil {
			t.Missing = domain.Known(int64(row.GetMissingCells()))
		}
		if row.ExcludedCells != nil {
			t.Excluded = domain.Known(int64(row.GetExcludedCells()))
		}
		for _, cell := range row.Cells {
			t.Cells = append(t.Cells, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
		result.Targets = append(result.Targets, t)
	}
	return result, true
}

func (r *RoutineHomeCoveragePlanner) step(call, epoch context.Context) (RoutineHomeCoverageResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineHomeCoverageResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineHomeCoverageResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineHomeCoverageResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainHomeCoverage {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineHomeCoverageResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineHomeCoverageResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineHomeCoverageResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, review.Tick)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	wanted, known := claims.Value()
	if !known {
		return RoutineHomeCoverageResult{}, ErrControl
	}
	construction := domain.Known(policy.CurrentConstruction{Requested: []string{}, Buildings: []policy.CurrentBuilding{}})
	if len(wanted) > 0 {
		ids := make([]string, 0, len(wanted))
		for _, claim := range wanted {
			ids = append(ids, claim.Identity.Current)
		}
		sort.Strings(ids)
		reply, _, err := r.native.ReadConstructionBuildings(call, identity, ids)
		if err != nil {
			return RoutineHomeCoverageResult{}, err
		}
		observed := reply.GetObserved()
		if observed == nil {
			return RoutineHomeCoverageResult{}, ErrControl
		}
		if err = bridge.ValidateConstructionBuildings(observed, identity, ids); err != nil {
			return RoutineHomeCoverageResult{}, err
		}
		if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
			return RoutineHomeCoverageResult{}, ErrControl
		}
		result := policy.CurrentConstruction{Requested: append([]string{}, ids...), Buildings: []policy.CurrentBuilding{}}
		for _, row := range observed.Buildings {
			building, err := domain.NewBuilding(row.Building.GetDefName(), domain.Cell{X: row.Building.Position.GetX(), Z: row.Building.Position.GetZ()}, domain.Rotation(strings.ToLower(row.GetRotation())), row.GetStuff())
			if err != nil {
				return RoutineHomeCoverageResult{}, err
			}
			result.Buildings = append(result.Buildings, policy.CurrentBuilding{ID: row.Building.GetId(), Building: building})
		}
		construction = domain.Known(result)
	}
	owned, err := policy.OwnedConstructions(claims, construction)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	zones, err := p.journal.StockpileClaims(call, state.Snapshot, review.Tick)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	reply, _, err := r.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineHomeCoverageResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineHomeCoverageResult{}, ErrControl
	}
	census, ok := homeCoverageObservationFacts(observed)
	if !ok {
		return RoutineHomeCoverageResult{Reason: BuildingMethodUsed}, nil
	}
	targets, err := policy.ReviewHomeCoverage(owned, zones, domain.Known(census))
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	choice, err := policy.SelectHomeCoverageMethod(targets, census.Revision, seen)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	if choice.Kind != policy.HomeCoverageExtend {
		return RoutineHomeCoverageResult{Reason: BuildingMethodUsed}, nil
	}
	coverage, err := domain.NewHomeCoverage(choice.Target, choice.Shape)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	id := domain.PlanID(fmt.Sprintf("routine-home-%s", choice.ID))
	action, err := domain.NewHomeCoverageAction(domain.ActionID(fmt.Sprintf("%s-0", id)), coverage)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineHomeCoverageResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	return RoutineHomeCoverageResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
