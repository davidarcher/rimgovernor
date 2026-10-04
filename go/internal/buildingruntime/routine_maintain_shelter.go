package buildingruntime

import (
	"context"
	"fmt"
	"slices"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ownedArea is one bot-owned area's committed cells and latest planned cells.
type ownedArea struct {
	current []domain.Cell
	known   bool
	want    []domain.Cell
}

// safeAreaMemory holds MaintainShelter's bot area state in memory only: the
// Safe area (#1325) and the NoDanger area (#1327, #1802), each with the cells last
// committed and the latest review's plan. A new world (start, reload)
// forgets the cells, so the first pass resets each area (delete, then
// create with the full set).
type safeAreaMemory struct {
	mu    sync.Mutex
	world string
	areas map[string]*ownedArea
	// edits is the latest review's plan for world.
	edits []domain.Area
	// last is the tick a live hostile was last seen in world (#1327).
	last      domain.Tick
	lastKnown bool
	// danger is the union of the danger seeds seen while the window held
	// (policy.DangerSeeds), forgotten once it closes.
	danger map[domain.Cell]bool
}

func (m *safeAreaMemory) enter(world string) {
	if m.world != world || m.areas == nil {
		// Field by field: overwriting *m would reset the held mutex.
		m.world, m.areas, m.edits, m.lastKnown, m.danger = world, map[string]*ownedArea{}, nil, false, nil
	}
}

func (m *safeAreaMemory) plan(key string, want []domain.Cell) error {
	a := m.areas[key]
	if a == nil {
		a = &ownedArea{}
		m.areas[key] = a
	}
	edits, err := policy.PlanBotArea(key, want, a.current, a.known)
	if err != nil {
		return err
	}
	a.want = want
	m.edits = append(m.edits, edits...)
	return nil
}

// review plans the bot areas from the review's rooms, home area and layout
// plan and reports whether an edit is owed; unknown when the rooms are. The
// NoDanger area is planned only while the home area is known and not empty.
func (m *safeAreaMemory) review(world string, projection observation.ColonyProjection) (domain.Fact[bool], error) {
	rooms, known := projection.Rooms.Value()
	if !known {
		return domain.Unknown[bool](), nil
	}
	var killbox, vet, isolation, barn []domain.Cell
	if plan, ok := projection.LayoutPlan.Value(); ok {
		killbox, vet, isolation, barn = plan.KillboxCells(), plan.VetRoomCells(), plan.IsolationRoomCells(), plan.BarnCells(rooms)
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.enter(world)
	m.edits = nil
	// The Safe area leaves the vet room out: an animal sheltered there
	// would enter it, and only the sterilize flow lets one in; nor does it
	// hold the isolation room, which only an isolated creepjoiner is kept in.
	if err := m.plan(policy.SafeAreaKey, policy.SafeAreaCells(rooms, slices.Concat(killbox, vet, isolation))); err != nil {
		return domain.Unknown[bool](), err
	}
	if len(vet) > 0 {
		if err := m.plan(policy.VetRoomAreaKey, vet); err != nil {
			return domain.Unknown[bool](), err
		}
	}
	// The barn's interior is the area pen animals are sheltered in while
	// their race is in danger outdoors (AnimalShelterChoice, #1869).
	if len(barn) > 0 {
		if err := m.plan(policy.BarnAreaKey, barn); err != nil {
			return domain.Unknown[bool](), err
		}
	}
	// The isolation room's interior is the area a creepjoiner is held in
	// (ManageCreepJoiners, #1740).
	if len(isolation) > 0 {
		if err := m.plan(policy.IsolationAreaKey, isolation); err != nil {
			return domain.Unknown[bool](), err
		}
	}
	if census, ok := projection.Facts.HomeCoverage.Value(); ok {
		if home, hk := census.Home.Value(); hk && len(home) > 0 {
			if err := m.plan(policy.NoDangerAreaKey, policy.NoDangerCells(home, killbox, sortedSeeds(m.danger))); err != nil {
				return domain.Unknown[bool](), err
			}
		}
	}
	return domain.Known(len(m.edits) > 0), nil
}

// take returns the pending edits for world; commit records their cells.
func (m *safeAreaMemory) take(world string) []domain.Area {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.enter(world)
	return m.edits
}

func (m *safeAreaMemory) commit(world string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.enter(world)
	for _, a := range m.areas {
		a.current, a.known = a.want, true
	}
	m.edits = nil
}

// dangerWindow records a live hostile at tick and reports whether haulers
// are kept out of the danger cells (policy.DangerWindowOf). While it holds,
// the threat census's danger seeds accumulate, so the cells a fight covered
// stay out for the cooldown; a closed window forgets them. A new world
// forgets the last threat, so a reload releases them.
func (m *safeAreaMemory) dangerWindow(world string, hostiles domain.Fact[int64], seeds []domain.Cell, tick domain.Tick) domain.Fact[bool] {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.enter(world)
	if n, known := hostiles.Value(); known && n > 0 {
		m.last, m.lastKnown = tick, true
	}
	window := policy.DangerWindowOf(hostiles, m.last, m.lastKnown, tick)
	if open, known := window.Value(); known && open {
		if m.danger == nil {
			m.danger = map[domain.Cell]bool{}
		}
		for _, c := range seeds {
			m.danger[c] = true
		}
	} else if known {
		m.danger = nil
	}
	return window
}

// dangerSeedFact is the review's danger seeds, unknown with the hostile count.
func dangerSeedFact(hostiles domain.Fact[int64], threats []policy.EmergencyThreat) domain.Fact[[]domain.Cell] {
	if _, known := hostiles.Value(); !known {
		return domain.Unknown[[]domain.Cell]()
	}
	return domain.Known(policy.DangerSeeds(threats))
}

func sortedSeeds(set map[domain.Cell]bool) []domain.Cell {
	out := make([]domain.Cell, 0, len(set))
	for c := range set {
		out = append(out, c)
	}
	slices.SortFunc(out, func(a, b domain.Cell) int {
		if a.Z != b.Z {
			return int(a.Z - b.Z)
		}
		return int(a.X - b.X)
	})
	return out
}

// MaintainShelterPlanner is MaintainShelter's planner: it commits the Safe
// area edits the review planned as one AreaIntent plan.
type MaintainShelterPlanner struct {
	reviewer *Rounder
}
type MaintainShelterResult struct {
	Verdict
	Plan domain.PlanID
}

func NewMaintainShelterPlanner(reviewer *Rounder) (*MaintainShelterPlanner, error) {
	if reviewer == nil || !reviewer.methodEnabled(policy.MaintainShelter) {
		return nil, fmt.Errorf("%w: NewMaintainShelterPlanner: reviewer == nil || !reviewer.methodEnabled(policy.MaintainShelter)", ErrControl)
	}
	return &MaintainShelterPlanner{reviewer}, nil
}

func (r *MaintainShelterPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (MaintainShelterResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return MaintainShelterResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return MaintainShelterResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return MaintainShelterResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return MaintainShelterResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainShelter)
	if err != nil {
		return MaintainShelterResult{}, err
	}
	if !workable {
		return MaintainShelterResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return MaintainShelterResult{}, err
		}
		if store.PlanOpen(plan) {
			return MaintainShelterResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	world := stockpileWorld(state.Snapshot)
	edits := r.reviewer.safeArea.take(world)
	if len(edits) == 0 {
		return MaintainShelterResult{Verdict: waitFor(WaitMethodUsed, "safe_area_edits")}, nil
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
	if _, err = p.journal.CommitGoalMethodReason(call, goal.Standard.ID, goal.Revision, method, fmt.Sprintf("bot areas %d edits", len(edits)), plan); err != nil {
		return MaintainShelterResult{}, err
	}
	r.reviewer.safeArea.commit(world)
	return MaintainShelterResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
