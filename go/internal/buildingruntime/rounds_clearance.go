package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsClearanceSource is the clearance census plus the zone preview the
// clearance plans need.
type RoundsClearanceSource interface {
	observation.ColonySource
	observation.ClearanceSource
	FieldNative
}

type RoundsClearancePlanner struct {
	reviewer *Rounder
	native   RoundsClearanceSource
}
type RoundsClearanceResult struct {
	Verdict
	Plan domain.PlanID
	// NativeWorkTicks asks for game time while pawns finish designated work.
	NativeWorkTicks uint32
}

func NewRoundsClearancePlanner(reviewer *Rounder, native RoundsClearanceSource) (*RoundsClearancePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsClearancePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsClearancePlanner{reviewer, native}, nil
}
func (r *RoundsClearancePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsClearanceResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsClearanceResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsClearanceResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsClearanceResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsClearanceResult{Verdict: BuildingReasonNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.ClearHomeObstructions, state.Snapshot, review.Tick)
	defer recorded()
	goal, workable, err := p.journal.Workable(call, review, policy.ClearHomeObstructions)
	if err != nil {
		return RoundsClearanceResult{}, err
	}
	if !workable {
		return RoundsClearanceResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsClearanceResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsClearanceResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoundsClearanceResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoundsClearanceResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsClearanceResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	colony, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoundsClearanceResult{}, err
	}
	ground := plannedGround(colony.Projection)
	read, err := observation.ObserveClearanceCensusOnGround(call, r.native, expected, true, ground)
	if err != nil {
		return RoundsClearanceResult{}, err
	}
	census, known := read.Value()
	if !known {
		return RoundsClearanceResult{Verdict: waitFor(policy.CauseMethodUsed, "clearance_census_unknown")}, nil
	}
	// The review's recovery queue ranks and admits; the planner executes its
	// removals as one roof-first batch against the fresh census, whose own
	// verdicts still gate every row.
	others, player := policy.SplitGroundRows(census.Targets)
	step := r.recoveryStep(call, boundary.Identity(state.Snapshot), review.RecoveryQueue, others, colony.Projection)
	id := domain.MintPlanID()
	var prefix string
	var actions []domain.Action
	if len(step.Roof) > 0 || len(step.Targets) > 0 {
		step = pendingClearanceStep(step)
		if len(step.Roof)+len(step.Targets) == 0 {
			return RoundsClearanceResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, nil
		}
		prefix, actions, err = recoveryStepMethod(id, step)
	} else if step, ok := plannedGroundStep(colony.Projection, stampPacking(player, colony.Projection), census.Floors, r.reviewer.clearFloors(colony.Projection)); ok {
		step = pendingClearanceStep(step)
		if len(step.Roof)+len(step.Targets)+len(step.Floors) == 0 {
			return RoundsClearanceResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, nil
		}
		prefix, actions, err = groundStepMethod(id, step)
	} else {
		return r.dump(census), nil
	}
	if err != nil {
		return RoundsClearanceResult{}, err
	}
	if verdict, ok, err := admitSubject(call, p.journal, prefix, standardMethodPlans(goal.History, goal.Standard.Episode, prefix), state.Snapshot); err != nil {
		return RoundsClearanceResult{}, err
	} else if !ok {
		return RoundsClearanceResult{Verdict: verdict}, nil
	}
	method := nextMethodID(prefix, historyMethodIDs(goal.History, goal.Standard.Episode))
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsClearanceResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsClearanceResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsClearanceResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsClearanceResult{}, err
	}
	return RoundsClearanceResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// pendingClearanceStep removes writes already present in native state, after
// the full step's roof safety and phase ordering have been checked. Standing
// targets remain a clearance deficit until pawn work actually removes them.
func pendingClearanceStep(step policy.GroundStep) policy.GroundStep {
	targets := make([]policy.ClearanceTarget, 0, len(step.Targets))
	for _, target := range step.Targets {
		if !target.Designated {
			targets = append(targets, target)
		}
	}
	step.Targets = targets
	floors := make([]policy.ClearanceFloor, 0, len(step.Floors))
	for _, floor := range step.Floors {
		if !floor.Designated {
			floors = append(floors, floor)
		}
	}
	step.Floors = floors
	return step
}

// plannedGroundStep is the next clearance method over the recorded plan and
// ground census; none while either is unknown, so nothing of the colony's comes
// down on a guess.
func plannedGroundStep(colony observation.ColonyProjection, player []policy.ClearanceTarget, floors []policy.ClearanceFloor, wants policy.RoomFloors) (policy.GroundStep, bool) {
	plan, known := colony.LayoutPlan.Value()
	if !known {
		return policy.GroundStep{}, false
	}
	ground, known := colonyGround(colony)
	if !known {
		return policy.GroundStep{}, false
	}
	return policy.PlannedGroundStep(plan, ground, player, floors, colonyRooms(colony), retiredGround(colony), wants)
}

// plannedGroundWork is the clearance deficit over the same plan and census;
// none while either is unknown.
func plannedGroundWork(colony observation.ColonyProjection, player []policy.ClearanceTarget, floors []policy.ClearanceFloor, wants policy.RoomFloors) []string {
	plan, known := colony.LayoutPlan.Value()
	if !known {
		return nil
	}
	ground, known := colonyGround(colony)
	if !known {
		return nil
	}
	return policy.PlannedGroundWork(plan, ground, player, floors, colonyRooms(colony), retiredGround(colony), wants)
}

// stampPacking marks the player rows that pack instead of deconstruct and the
// ones in use (#2103): packable from the def mirror, in use as an owned bed or
// a bench with an active bill.
func stampPacking(rows []policy.ClearanceTarget, colony observation.ColonyProjection) []policy.ClearanceTarget {
	inUse := map[string]bool{}
	if sleeping, known := colony.Facts.Sleeping.Value(); known {
		for _, bed := range sleeping.Beds {
			inUse[bed.ID] = inUse[bed.ID] || len(bed.Owners) > 0
		}
	}
	if benches, known := colony.ProductionBenches.Value(); known {
		for _, bench := range benches {
			for _, bill := range bench.Bills {
				if active, _ := bill.Active.Value(); active {
					inUse[bench.ID] = true
				}
			}
		}
	}
	out := append([]policy.ClearanceTarget(nil), rows...)
	for i := range out {
		out[i].Packable = colony.Packable[out[i].DefName]
		out[i].InUse = inUse[out[i].EntityID]
	}
	return out
}

func colonyRooms(colony observation.ColonyProjection) policy.RoomObservation {
	rooms, _ := colony.Rooms.Value()
	return rooms
}

// groundStepMethod is a clearance step's method prefix and actions. Furniture
// comes down one building per method, as home clearance does, so removals
// cannot jointly invalidate the observed roof support; packing, floors and the
// ready walls of every room go in one batch each, the roof first (#1366). A
// door is swapped in place, one per method for a wall of its own stuff.
func groundStepMethod(id domain.PlanID, step policy.GroundStep) (string, []domain.Action, error) {
	var prefix string
	switch step.Phase {
	case policy.GroundFurniture:
		prefix = fmt.Sprintf("deconstruct-%s-", step.Targets[0].EntityID)
	case policy.GroundPack:
		prefix = batchPrefix("pack", step.Targets[0].EntityID, len(step.Targets))
	case policy.GroundDoors:
		prefix = fmt.Sprintf("swap-door-%s-", step.Targets[0].EntityID)
	case policy.GroundWalls:
		switch {
		case len(step.Roof) > 0 && len(step.Targets) == 0:
			prefix = fmt.Sprintf("roof-off-%d-%d-x%d-", step.Roof[0].X, step.Roof[0].Z, len(step.Roof))
		case step.Ground != (policy.Rectangle{}):
			prefix = fmt.Sprintf("ground-walls-%d-%d-", step.Ground.X, step.Ground.Z)
		default:
			prefix = batchPrefix("ground-walls", step.Targets[0].EntityID, len(step.Targets))
		}
	default:
		c := step.Floors[0].Cell
		prefix = fmt.Sprintf("ground-floors-%d-%d-x%d-", c.X, c.Z, len(step.Floors))
	}
	actions, err := groundActions(id, step)
	return prefix, actions, err
}

func batchPrefix(kind, first string, n int) string {
	if n > 1 {
		return fmt.Sprintf("%s-%s-x%d-", kind, first, n)
	}
	return fmt.Sprintf("%s-%s-", kind, first)
}

// groundActions is the step's intents in order: remove_roof first, then
// one deconstruction per target, then one floor
// removal per floor cell.
func groundActions(id domain.PlanID, step policy.GroundStep) ([]domain.Action, error) {
	var actions []domain.Action
	next := func() domain.ActionID { return domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))) }
	if len(step.Roof) > 0 {
		roof, err := domain.NewRemoveRoof(step.Roof)
		if err != nil {
			return nil, err
		}
		action, err := domain.NewRemoveRoofAction(next(), roof)
		if err != nil {
			return nil, err
		}
		actions = append(actions, action)
	}
	for _, target := range step.Targets {
		if step.Phase == policy.GroundPack {
			// Native resolves the piece by id and echoes its own placement.
			value, err := domain.NewMoveBuilding(target.EntityID, target.DefName, target.Minimum, domain.South)
			if err != nil {
				return nil, err
			}
			action, err := domain.NewUninstallBuildingAction(next(), value)
			if err != nil {
				return nil, err
			}
			actions = append(actions, action)
			continue
		}
		value, err := domain.NewDeconstruction(target.EntityID, target.DefName, target.Minimum)
		if err != nil {
			return nil, err
		}
		if step.Phase == policy.GroundDoors {
			value = value.WithWallReplacement()
		}
		action, err := domain.NewDeconstructionAction(next(), value)
		if err != nil {
			return nil, err
		}
		actions = append(actions, action)
	}
	for _, floor := range step.Floors {
		value, err := domain.NewFloorRemoval(floor.DefName, floor.Cell)
		if err != nil {
			return nil, err
		}
		action, err := domain.NewFloorRemovalAction(next(), value)
		if err != nil {
			return nil, err
		}
		actions = append(actions, action)
	}
	return actions, nil
}

// dump is the chunk half of the clearance goal (#394): chunks are hauls, not
// deconstructions. A pending stack (in Home, allowed, unstored, no store will
// take it) has no store yet: the materials yard takes chunks and slag, so the
// planner makes no zone and refuses no_space until one stands. Once a store
// takes a stack, ordinary hauling moves it: the native mod marks chunk defs
// always haulable (#2513), so no designation is ordered.
func (r *RoundsClearancePlanner) dump(census policy.ClearanceCensus) RoundsClearanceResult {
	if len(policy.PendingChunks(census.Chunks)) == 0 {
		return RoundsClearanceResult{Verdict: waitFor(policy.CauseMethodUsed, "chunk_haul_chunks")}
	}
	return RoundsClearanceResult{Verdict: noSpace("chunk_dump_site")}
}
