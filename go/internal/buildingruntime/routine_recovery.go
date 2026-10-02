package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineRecoveryPlanner reconciles colonist and animal areas from current
// hazards before proposing repair/breakdown/refuel work. Area correction needs
// no disaster history. Colonists use WorkSettingsIntent and animals use husbandry's
// current-settings/census CAS, both through ordinary Hands admission.
//
// Unlike RoutineGearPlanner, this planner re-derives its selection with a
// fresh full colony census (observation.ObserveRoutineOwned against the
// reviewer's own RoutineSource, not a narrower per-family source) because
// RecoveryWorkers requires the mood-pawn census only that full pipeline
// produces; see routine_recovery.go in package store for the analogous
// review-cycle computation this mirrors at dispatch time.
type RoutineRecoveryPlanner struct {
	reviewer *RoutineReviewer
}
type RoutineRecoveryResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// Sheltered reports policy.ShelterHeld at this step; the clock
	// window watches a threat the colony is sheltered from (#1560).
	Sheltered bool
}

func NewRoutineRecoveryPlanner(reviewer *RoutineReviewer) (*RoutineRecoveryPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoutineRecoveryPlanner: reviewer == nil", ErrControl)
	}
	return &RoutineRecoveryPlanner{reviewer}, nil
}

func recoveryServiceMethod(method policy.RecoveryMethod) (domain.RecoveryMethod, bool) {
	switch method {
	case policy.RecoveryRepair:
		return domain.RecoveryServiceRepair, true
	case policy.RecoveryBreakdown:
		return domain.RecoveryServiceBreakdown, true
	case policy.RecoveryRefuel:
		return domain.RecoveryServiceRefuel, true
	default:
		return "", false
	}
}

func (r *RoutineRecoveryPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (result RoutineRecoveryResult, err error) {
	sheltered := false
	defer func() {
		if err == nil {
			result.Sheltered = sheltered
		}
	}()
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineRecoveryResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineRecoveryResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineRecoveryResult{Reason: BuildingMethodNoReview}, nil
	}
	incident, found, err := incidentDeficit(call, p.journal, review, policy.RecoverDisasterServices)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if !found {
		return RoutineRecoveryResult{Reason: BuildingMethodNoDeficit}, nil
	}
	var open []store.PlanState
	for _, method := range incident.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineRecoveryResult{}, err
		}
		if store.PlanOpen(plan) {
			open = append(open, plan)
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineRecoveryResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	facts := read.Projection.Facts
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, read.Emergency)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	facts.Hostiles, _ = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	facts.KillboxWindow = r.reviewer.safeArea.killboxWindow(stockpileWorld(state.Snapshot), facts.Hostiles, expected.Tick)
	facts.KillboxHaulers = policy.KillboxHaulers(read.Projection.WorkPawns)
	world := store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}
	if facts.ShelterCombatants, err = shelterCombatants(call, p.journal, world); err != nil {
		return RoutineRecoveryResult{}, err
	}
	changes := policy.PlanSheltering(facts)
	sheltered = policy.ShelterHeld(facts)
	workers, _ := read.Projection.WorkPawns.Value()
	if err = p.current(call, epoch); err != nil {
		return RoutineRecoveryResult{}, err
	}
	if p.session.State() != state {
		return RoutineRecoveryResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	for _, plan := range open {
		if err := cancelStaleAreaActions(call, p.journal, plan, changes, workers); err != nil {
			return RoutineRecoveryResult{}, err
		}
		updated, err := p.journal.LoadPlan(call, plan.Spec.ID())
		if err != nil {
			return RoutineRecoveryResult{}, err
		}
		if domain.GoalWorkOpen(updated.Progress) {
			return RoutineRecoveryResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	if len(changes) > 0 {
		return r.commitAreaChange(call, epoch, arbiter, state.Snapshot, incident, changes, workers, started)
	}
	planning := policy.RecoveryPlanning{Safety: facts.RecoverySafety, Workers: facts.RecoveryWorkers, Buildings: facts.RecoveryBuildings}
	seen := make([]domain.MethodID, 0, len(incident.Methods))
	for _, method := range incident.Methods {
		seen = append(seen, method.Method)
	}
	selection, err := policy.SelectRecoveryMethods(planning, review.Disaster, seen, expected.Tick)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if err = selection.Validate(); err != nil {
		return RoutineRecoveryResult{}, err
	}
	var chosen *policy.RecoveryCandidate
	for i := range selection.Candidates {
		if selection.Candidates[i].Kind == policy.RecoveryServiceProposal {
			chosen = &selection.Candidates[i]
			break
		}
	}
	if chosen == nil {
		return RoutineRecoveryResult{Reason: BuildingMethodUsed}, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(chosen.Pawn)}) {
		return RoutineRecoveryResult{Reason: BuildingMethodUsed}, nil
	}
	id := domain.MintPlanID()
	method, ok := recoveryServiceMethod(chosen.Method)
	if !ok {
		return RoutineRecoveryResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
	}
	service, err := domain.NewRecoveryService(domain.PawnID(chosen.Pawn), chosen.Building, method)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	action, err := domain.NewRecoveryServiceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), service)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineRecoveryResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineRecoveryResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, chosen.ID, "", plan); err != nil {
		return RoutineRecoveryResult{}, err
	}
	return RoutineRecoveryResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// shelterCombatants is the squad's draft set for sheltering (#1367): the
// rosters of this world's open combat fights, the pawns ActiveCombat drafted
// from SelectSquadDefense's assignments. It is known and empty while no fight
// is open, so a threat the squad does not fight (a manhunter pack waited out)
// shelters every colonist; a pawn drafted later is skipped as drafted.
func shelterCombatants(ctx context.Context, journal *store.Store, world store.World) (domain.Fact[[]policy.PawnID], error) {
	fights, err := journal.OpenCombatFights(ctx)
	if err != nil {
		return domain.Unknown[[]policy.PawnID](), err
	}
	out := []policy.PawnID{}
	for _, fight := range fights {
		if fight.World != world {
			continue
		}
		for pawn := range fight.Roster {
			out = append(out, policy.PawnID(pawn))
		}
	}
	slices.Sort(out)
	return domain.Known(out), nil
}
