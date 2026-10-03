package buildingruntime

import (
	"context"
	"fmt"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ManageCreepJoiners (#1740, epic #1694): a creepjoiner's downside is hidden
// until it shows, so until then the colonist holds no weapon. Each review
// reads the frame's colonist rows (policy.CreepJoinerDownsides.WeaponDrops)
// and journals whether a drop is owed; the planner orders each such colonist
// to drop the weapon in its hands (domain.DropEquipment). The equipment
// planner, the armory and the fight loadout leave the colonist unarmed
// (policy.EquipCandidatePawn.NoArms) until the downside shows.

// creepJoinerMemory is the latest review's drops for one world, in memory only.
type creepJoinerMemory struct {
	mu    sync.Mutex
	world string
	tick  domain.Tick
	drops []policy.WeaponDrop
	known bool
}

// review finds the drops the colony owes from the frame's colonist rows and
// the catalog's downside defs, and returns whether any is owed: unknown
// without the frame's pawn detail.
func (m *creepJoinerMemory) review(current domain.GenerationSnapshot, tick domain.Tick, frame bridge.RoutineFrame) domain.Fact[bool] {
	owed := domain.Unknown[bool]()
	var drops []policy.WeaponDrop
	// The frame's colonist rows count only against a complete roster, as
	// the review's other pawn facts do (observation.routinePawns).
	if complete, known := frame.Emergency.Facts.ColonistsComplete.Value(); frame.Pawns != nil && known && complete && len(frame.Pawns.Pawns) == len(frame.Emergency.Facts.Colonists) {
		hands := make([]policy.CreepJoinerHand, 0, len(frame.Pawns.Pawns))
		for _, row := range frame.Pawns.Pawns {
			hands = append(hands, bridge.CreepJoinerHand(row))
		}
		drops, owed = frame.Catalog.CreepJoinerDownsides().WeaponDrops(hands)
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.world, m.tick, m.drops = stockpileWorld(current), tick, drops
	_, m.known = owed.Value()
	return owed
}

// take returns the drops the review at tick left for world.
func (m *creepJoinerMemory) take(world string, tick domain.Tick) ([]policy.WeaponDrop, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.world != world || m.tick != tick || !m.known {
		return nil, false
	}
	return append([]policy.WeaponDrop(nil), m.drops...), true
}

// RoutineCreepJoinerPlanner is ManageCreepJoiners's planner.
type RoutineCreepJoinerPlanner struct {
	reviewer *RoutineReviewer
	memory   *creepJoinerMemory
}
type RoutineCreepJoinerResult struct {
	Verdict
	Plan domain.PlanID
}

// NewRoutineCreepJoinerPlanner composes the planner and has reviewer run the
// creepjoiner review over each frame.
func NewRoutineCreepJoinerPlanner(reviewer *RoutineReviewer) (*RoutineCreepJoinerPlanner, error) {
	if reviewer == nil || !reviewer.methodEnabled(policy.ManageCreepJoiners) {
		return nil, fmt.Errorf("%w: NewRoutineCreepJoinerPlanner: reviewer == nil || !reviewer.methodEnabled(policy.ManageCreepJoiners)", ErrControl)
	}
	reviewer.creepJoiners = &creepJoinerMemory{}
	return &RoutineCreepJoinerPlanner{reviewer, reviewer.creepJoiners}, nil
}

func (r *RoutineCreepJoinerPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineCreepJoinerResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineCreepJoinerResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineCreepJoinerResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineCreepJoinerResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineCreepJoinerResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.ManageCreepJoiners)
	if err != nil {
		return RoutineCreepJoinerResult{}, err
	}
	if !workable {
		return RoutineCreepJoinerResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineCreepJoinerResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineCreepJoinerResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	drops, ok := r.memory.take(stockpileWorld(state.Snapshot), review.Tick)
	if !ok {
		return RoutineCreepJoinerResult{Verdict: BuildingReasonNoReview}, nil
	}
	// One plan of independent drops: no colonist waits for another's.
	id := domain.MintPlanID()
	var actions []domain.Action
	var claimed []domain.PawnID
	for _, drop := range drops {
		pawn := domain.PawnID(drop.Pawn)
		if !arbiter.tryClaim([]domain.PawnID{pawn}) {
			continue
		}
		claimed = append(claimed, pawn)
		order, err := domain.NewDropEquipment(pawn, drop.Weapon)
		if err != nil {
			return RoutineCreepJoinerResult{}, err
		}
		action, err := domain.NewDropEquipmentAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), order)
		if err != nil {
			return RoutineCreepJoinerResult{}, err
		}
		actions = append(actions, action)
	}
	if len(actions) == 0 {
		return RoutineCreepJoinerResult{Verdict: BuildingReasonUsed}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineCreepJoinerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineCreepJoinerResult{}, err
	}
	if p.session.State() != state {
		return RoutineCreepJoinerResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	method := nextWaveMethod(goal, "creepjoiner-drop-")
	reason := fmt.Sprintf("creepjoiner weapon: %v drop the weapon they hold until their downside shows", claimed)
	if _, err = p.journal.CommitGoalMethodReason(call, goal.Goal.ID, goal.Revision, method, reason, plan); err != nil {
		return RoutineCreepJoinerResult{}, err
	}
	return RoutineCreepJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
