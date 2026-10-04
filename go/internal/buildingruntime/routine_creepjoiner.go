package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ManageCreepJoiners (#1740, epic #1694): a creepjoiner's downside is hidden
// until it shows, so until then the colonist holds no weapon and gets a
// surgical inspection. Each review reads the frame's colonist rows
// (policy.CreepJoinerDownsides.WeaponDrops and Inspections) and journals
// whether work is owed; the planner orders each such colonist to drop the
// weapon in its hands (domain.DropEquipment) and queues the inspection
// (domain.Surgery), and keeps the colony's inspection record on the goal
// (policy.CreepJoinerRecord). The equipment planner, the armory and the
// fight loadout leave the colonist unarmed (policy.EquipCandidatePawn.NoArms)
// until the downside shows.

// creepJoinerWork is what one review found the colony owes its creepjoiners.
type creepJoinerWork struct {
	drops       []policy.WeaponDrop
	inspections policy.Inspections
	isolation   []policy.IsolationMove
	disarm      policy.DisarmWork
}

// creepJoinerMemory is the latest review's work for one world, in memory only.
type creepJoinerMemory struct {
	mu    sync.Mutex
	world string
	tick  domain.Tick
	work  creepJoinerWork
	known bool
}

// creepJoinerRecord is the colony's inspection record on the goal the latest
// review binds to ManageCreepJoiners; empty while there is none.
func creepJoinerRecord(ctx context.Context, journal *store.Store) (policy.CreepJoinerRecord, error) {
	review, err := journal.LoadRoutineReview(ctx)
	if errors.Is(err, store.ErrNotFound) {
		return policy.CreepJoinerRecord{}, nil
	}
	if err != nil {
		return policy.CreepJoinerRecord{}, err
	}
	for _, binding := range review.Goals {
		if binding.Need != policy.ManageCreepJoiners {
			continue
		}
		state, err := journal.LoadGoal(ctx, binding.Goal)
		if err != nil {
			return policy.CreepJoinerRecord{}, err
		}
		return policy.ParseCreepJoinerRecord(state.Goal.Record)
	}
	return policy.CreepJoinerRecord{}, nil
}

// review finds the work the colony owes from the frame's colonist rows, the
// catalog's defs and the goal's record, and returns whether any is owed:
// unknown without the frame's pawn detail.
func (m *creepJoinerMemory) review(current domain.GenerationSnapshot, tick domain.Tick, facts observation.ColonyProjection, frame bridge.RoutineFrame, record policy.CreepJoinerRecord) domain.Fact[bool] {
	owed := domain.Unknown[bool]()
	var work creepJoinerWork
	if hands, ok := creepJoinerHands(frame); ok {
		downsides := frame.Catalog.CreepJoinerDownsides()
		work.drops, owed = downsides.WeaponDrops(hands)
		work.inspections = downsides.Inspections(hands, frame.Catalog.SurgicalInspectionRecipes(), record)
		if work.inspections.Owed(record) {
			owed = domain.Known(true)
		}
		// Held apart or released by the record this review leaves.
		area, _ := facts.Facts.IsolationArea.Value()
		work.isolation = downsides.IsolationMoves(hands, work.inspections.Record, area, isolationReady(facts))
		if len(work.isolation) > 0 {
			owed = domain.Known(true)
		}
	}
	// Arrested creepjoiners are disarmed from the prisoner census.
	if prisoners, known := facts.Facts.Prisoners.Value(); known {
		if disarm, err := frame.Catalog.CreepJoinerDisarm(); err != nil {
			clockSchedulerLog("creepjoiner disarm: %v", err)
		} else if work.disarm = disarm.Orders(prisoners); len(work.disarm.Orders) > 0 {
			owed = domain.Known(true)
		}
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.world, m.tick, m.work = stockpileWorld(current), tick, work
	_, m.known = owed.Value()
	return owed
}

// creepJoinerHands are the colonist rows' creepjoiner facts. The frame's
// colonist rows count only against a complete roster, as the review's other
// pawn facts do (observation.routinePawns).
func creepJoinerHands(frame bridge.RoutineFrame) ([]policy.CreepJoinerHand, bool) {
	complete, known := frame.Emergency.Facts.ColonistsComplete.Value()
	if frame.Pawns == nil || !known || !complete || len(frame.Pawns.Pawns) != len(frame.Emergency.Facts.Colonists) {
		return nil, false
	}
	hands := make([]policy.CreepJoinerHand, 0, len(frame.Pawns.Pawns))
	for _, row := range frame.Pawns.Pawns {
		hands = append(hands, bridge.CreepJoinerHand(row))
	}
	return hands, true
}

// isolation is the projection's isolation inputs: the creepjoiners held
// apart (unknown without the complete pawn rows) and the sleeping beds the
// room can be furnished from.
func creepJoinerIsolation(facts observation.ColonyProjection, frame bridge.RoutineFrame, record policy.CreepJoinerRecord) policy.IsolationPlanning {
	out := policy.IsolationPlanning{Pawns: domain.Unknown[[]policy.PawnID](), Beds: facts.Shapes.Furniture.SleepingBeds()}
	if hands, ok := creepJoinerHands(frame); ok {
		// The record before this review's inspection update: a bill that ended
		// this review releases the pawn the next one.
		out.Pawns = frame.Catalog.CreepJoinerDownsides().Isolated(hands, record)
	}
	return out
}

// isolationReady is whether the isolation room stands with its bed: only
// then is a creepjoiner held in its area.
func isolationReady(facts observation.ColonyProjection) bool {
	need, owed := isolationRoomNeed(facts)
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !owed || !pk || !rk || !ck || !census.Colony {
		return false
	}
	return policy.IsolationRoomStanding(plan, rooms, census.Buildings, need, furnitureDefinitions(facts))
}

// take returns the work the review at tick left for world.
func (m *creepJoinerMemory) take(world string, tick domain.Tick) (creepJoinerWork, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.world != world || m.tick != tick || !m.known {
		return creepJoinerWork{}, false
	}
	return m.work, true
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
	work, ok := r.memory.take(stockpileWorld(state.Snapshot), review.Tick)
	if !ok {
		return RoutineCreepJoinerResult{Verdict: BuildingReasonNoReview}, nil
	}
	was, err := policy.ParseCreepJoinerRecord(goal.Goal.Record)
	if err != nil {
		return RoutineCreepJoinerResult{}, err
	}
	// One plan of independent drops, inspections and area moves: no colonist
	// waits for another's.
	id := domain.MintPlanID()
	var actions []domain.Action
	var claimed []domain.PawnID
	for _, drop := range work.drops {
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
	for _, move := range work.isolation {
		pawn := domain.PawnID(move.Pawn)
		if !arbiter.tryClaim([]domain.PawnID{pawn}) {
			continue
		}
		claimed = append(claimed, pawn)
		assignment, err := domain.NewAreaAssignment(pawn, move.Area == "", move.Area)
		if err != nil {
			return RoutineCreepJoinerResult{}, err
		}
		action, err := domain.NewWorkAssignmentAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), assignment)
		if err != nil {
			return RoutineCreepJoinerResult{}, err
		}
		actions = append(actions, action)
	}
	ordered := work.inspections.Record
	for _, order := range work.inspections.Orders {
		pawn := domain.PawnID(order.Pawn)
		if !arbiter.tryClaim([]domain.PawnID{pawn}) {
			// Not ordered this time: the record must not say it was.
			delete(ordered.Inspections, order.Pawn)
			continue
		}
		claimed = append(claimed, pawn)
		surgery, err := domain.NewSurgery(pawn, order.Recipe, order.Part, false)
		if err != nil {
			return RoutineCreepJoinerResult{}, err
		}
		action, err := domain.NewSurgeryAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), surgery)
		if err != nil {
			return RoutineCreepJoinerResult{}, err
		}
		actions = append(actions, action)
	}
	for _, order := range work.disarm.Orders {
		pawn := domain.PawnID(order.Pawn)
		if !arbiter.tryClaim([]domain.PawnID{pawn}) {
			continue
		}
		claimed = append(claimed, pawn)
		surgery, err := domain.NewSurgery(pawn, order.Recipe, order.Part, false)
		if err != nil {
			return RoutineCreepJoinerResult{}, err
		}
		action, err := domain.NewSurgeryAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), surgery)
		if err != nil {
			return RoutineCreepJoinerResult{}, err
		}
		actions = append(actions, action)
	}
	if len(actions) == 0 {
		for _, why := range work.disarm.Waiting {
			clockSchedulerLog("%s: %s", goal.Goal.ID, why)
		}
		// No order to place: an ended inspection bill still changes the record.
		if ordered.Encode() != was.Encode() {
			if err = p.current(call, epoch); err != nil {
				return RoutineCreepJoinerResult{}, err
			}
			if _, err = p.journal.RecordGoal(call, goal.Goal.ID, goal.Revision, ordered.Encode()); err != nil {
				return RoutineCreepJoinerResult{}, err
			}
			return RoutineCreepJoinerResult{Verdict: waitFor(WaitMethodUsed, "creepjoiner_order_recorded")}, nil
		}
		if len(work.inspections.Waiting) > 0 {
			clockSchedulerLog("%s: %s", goal.Goal.ID, work.inspections.Waiting[0])
			return RoutineCreepJoinerResult{Verdict: awaitingPlan("creepjoiner_inspection", "")}, nil
		}
		return RoutineCreepJoinerResult{Verdict: waitFor(WaitMethodUsed, "creepjoiner_order")}, nil
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
	reason := fmt.Sprintf("creepjoiner: %v are held back (weapon dropped, surgical inspection, isolation room, arrested ones disarmed) until their downside shows", claimed)
	if _, err = p.journal.CommitGoalMethodRecord(call, goal.Goal.ID, goal.Revision, method, reason, plan, ordered.Encode()); err != nil {
		return RoutineCreepJoinerResult{}, err
	}
	return RoutineCreepJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
