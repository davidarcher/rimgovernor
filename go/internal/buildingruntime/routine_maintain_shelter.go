package buildingruntime

import (
	"context"
	"fmt"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// safeAreaMemory holds MaintainShelter's Safe area state (#1325) in memory
// only: the cells last committed, and the edits the latest review planned.
// A new world (start, reload) forgets the cells, so the first pass resets
// the area (delete, then create with the full set).
type safeAreaMemory struct {
	mu      sync.Mutex
	world   string
	current []domain.Cell
	known   bool
	// pending is the latest review's plan for world: its edits and the
	// cells they leave.
	edits []domain.Area
	want  []domain.Cell
}

func (m *safeAreaMemory) enter(world string) {
	if m.world != world {
		// Field by field: overwriting *m would reset the held mutex.
		m.world, m.current, m.known, m.edits, m.want = world, nil, false, nil, nil
	}
}

// review plans the Safe area from the review's rooms and layout plan and
// reports whether an edit is owed; unknown when the rooms are.
func (m *safeAreaMemory) review(world string, projection observation.ColonyProjection) (domain.Fact[bool], error) {
	rooms, known := projection.Rooms.Value()
	if !known {
		return domain.Unknown[bool](), nil
	}
	var killbox []domain.Cell
	if plan, ok := projection.LayoutPlan.Value(); ok {
		killbox = plan.KillboxCells()
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.enter(world)
	edits, want, err := policy.PlanSafeArea(rooms, killbox, m.current, m.known)
	if err != nil {
		return domain.Unknown[bool](), err
	}
	m.edits, m.want = edits, want
	return domain.Known(len(edits) > 0), nil
}

// take returns the pending edits for world; commit records their cells.
func (m *safeAreaMemory) take(world string) ([]domain.Area, []domain.Cell) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.enter(world)
	return m.edits, m.want
}

func (m *safeAreaMemory) commit(world string, cells []domain.Cell) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.enter(world)
	m.current, m.known, m.edits = cells, true, nil
}

// MaintainShelterPlanner is MaintainShelter's planner: it commits the Safe
// area edits the review planned as one AreaIntent plan.
type MaintainShelterPlanner struct {
	reviewer *RoutineReviewer
}
type MaintainShelterResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewMaintainShelterPlanner(reviewer *RoutineReviewer) (*MaintainShelterPlanner, error) {
	if reviewer == nil || !reviewer.methodEnabled(policy.MaintainShelter) {
		return nil, fmt.Errorf("%w: NewMaintainShelterPlanner: reviewer == nil || !reviewer.methodEnabled(policy.MaintainShelter)", ErrControl)
	}
	return &MaintainShelterPlanner{reviewer}, nil
}

func (r *MaintainShelterPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (MaintainShelterResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return MaintainShelterResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return MaintainShelterResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return MaintainShelterResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return MaintainShelterResult{Reason: BuildingMethodNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainShelter)
	if err != nil {
		return MaintainShelterResult{}, err
	}
	if !workable {
		return MaintainShelterResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return MaintainShelterResult{}, err
		}
		if store.PlanOpen(plan) {
			return MaintainShelterResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	world := stockpileWorld(state.Snapshot)
	edits, want := r.reviewer.safeArea.take(world)
	if len(edits) == 0 {
		return MaintainShelterResult{Reason: BuildingMethodUsed}, nil
	}
	id := domain.MintPlanID()
	actions := make([]domain.Action, 0, len(edits))
	for i, edit := range edits {
		action, err := domain.NewAreaAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), edit)
		if err != nil {
			return MaintainShelterResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return MaintainShelterResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return MaintainShelterResult{}, err
	}
	if p.session.State() != state {
		return MaintainShelterResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	method := domain.MethodID(fmt.Sprintf("safe-area-%s", id))
	if _, err = p.journal.CommitGoalMethodReason(call, goal.Goal.ID, goal.Revision, method, fmt.Sprintf("safe area %d cells", len(want)), plan); err != nil {
		return MaintainShelterResult{}, err
	}
	r.reviewer.safeArea.commit(world, want)
	return MaintainShelterResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
