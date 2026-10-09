package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsRecoveryPlanner reconciles colonist and animal areas from current
// hazards before proposing repair/breakdown/refuel work. Area correction needs
// no disaster history. Colonists use WorkSettingsIntent and animals use husbandry's
// current-settings/census CAS, both through ordinary Hands admission.
//
// Unlike RoundsGearPlanner, this planner re-derives its selection with a
// fresh full colony census (observation.ObserveRoundsOwned against the
// reviewer's own RoundsSource, not a narrower per-family source) because
// RecoveryWorkers requires the mood-pawn census only that full pipeline
// produces; see rounds_recovery.go in package store for the analogous
// review-cycle computation this mirrors at dispatch time.
type RoundsRecoveryPlanner struct {
	reviewer *Rounder
}
type RoundsRecoveryResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsRecoveryPlanner(reviewer *Rounder) (*RoundsRecoveryPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoundsRecoveryPlanner: reviewer == nil", ErrControl)
	}
	return &RoundsRecoveryPlanner{reviewer}, nil
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

func (r *RoundsRecoveryPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (result RoundsRecoveryResult, err error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsRecoveryResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsRecoveryResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsRecoveryResult{Verdict: BuildingReasonNoReview}, nil
	}
	incident, found, err := incidentDeficit(call, p.journal, review, policy.RecoverDisasterServices)
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	if !found {
		return RoundsRecoveryResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	var open []store.PlanState
	var applied []domain.RecoveryService
	for _, method := range incident.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsRecoveryResult{}, err
		}
		if store.PlanOpen(plan) {
			open = append(open, plan)
		}
		for i, action := range plan.Spec.Actions() {
			if service, ok := action.RecoveryService(); ok && i < len(plan.Progress) && plan.Progress[i].View().Stage == domain.Completed {
				applied = append(applied, service)
			}
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsRecoveryResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	facts := read.Projection.Facts
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, read.Emergency)
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	facts.Hostiles, _ = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	facts.DangerSeeds = dangerSeedFact(facts.Hostiles, read.Emergency.Threats)
	seeds, _ := facts.DangerSeeds.Value()
	facts.DangerWindow = r.reviewer.safeArea.dangerWindow(stockpileWorld(state.Snapshot), facts.Hostiles, seeds, expected.Tick)
	facts.DangerHaulers = policy.DangerHaulers(read.Projection.WorkPawns)
	world := store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}
	if facts.ShelterCombatants, err = shelterCombatants(call, p.journal, world); err != nil {
		return RoundsRecoveryResult{}, err
	}
	changes := policy.PlanSheltering(facts)
	workers, _ := read.Projection.WorkPawns.Value()
	if err = p.current(call, epoch); err != nil {
		return RoundsRecoveryResult{}, err
	}
	if p.session.State() != state {
		return RoundsRecoveryResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	for _, plan := range open {
		if err := cancelStaleAreaActions(call, p.journal, plan, changes, workers); err != nil {
			return RoundsRecoveryResult{}, err
		}
		updated, err := p.journal.LoadPlan(call, plan.Spec.ID())
		if err != nil {
			return RoundsRecoveryResult{}, err
		}
		if recoveryWorkBlocksProtection(updated.Progress, review.Immediate) {
			return RoundsRecoveryResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	if len(changes) > 0 {
		return r.commitAreaChange(call, epoch, arbiter, state.Snapshot, incident, changes, workers, started)
	}
	if review.Immediate {
		return RoundsRecoveryResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, service := range applied {
		if continuing, known := policy.RecoveryWorkContinuing(service, facts.RecoveryBuildings, read.Projection.WorkPawns).Value(); !known {
			return RoundsRecoveryResult{Verdict: fieldUnavailable("recovery_work")}, nil
		} else if continuing {
			return RoundsRecoveryResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	planning := policy.RecoveryPlanning{Safety: facts.RecoverySafety, Workers: facts.RecoveryWorkers, Buildings: facts.RecoveryBuildings}
	seen := make([]domain.MethodID, 0, len(incident.Methods))
	for _, method := range incident.Methods {
		seen = append(seen, method.Method)
	}
	selection, err := policy.SelectRecoveryMethods(planning, review.Disaster, seen, review.Tick)
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	if err = selection.Validate(); err != nil {
		return RoundsRecoveryResult{}, err
	}
	var chosen *policy.RecoveryCandidate
	for i := range selection.Candidates {
		if selection.Candidates[i].Kind == policy.RecoveryServiceProposal {
			chosen = &selection.Candidates[i]
			break
		}
	}
	if chosen == nil {
		return RoundsRecoveryResult{Verdict: recoverySelectionVerdict(selection.Reason)}, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(chosen.Pawn)}) {
		return RoundsRecoveryResult{Verdict: waitFor(WaitMethodUsed, "recovery_pawn_claim")}, nil
	}
	id := domain.MintPlanID()
	method, ok := recoveryServiceMethod(chosen.Method)
	if !ok {
		return RoundsRecoveryResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
	}
	service, err := domain.NewRecoveryService(domain.PawnID(chosen.Pawn), chosen.Building, method)
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	action, err := domain.NewRecoveryServiceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), service)
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsRecoveryResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsRecoveryResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, chosen.ID, "", plan); err != nil {
		return RoundsRecoveryResult{}, err
	}
	return RoundsRecoveryResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// Preserve the policy's continuation reason in the existing planner_step row.
func recoverySelectionVerdict(reason policy.RecoverySelectionReason) Verdict {
	switch reason {
	case policy.RecoveryFactsUnknown:
		return fieldUnavailable("recovery_observations")
	case policy.RecoveryNoWorker:
		return noWorker("recovery_service")
	case policy.RecoveryTrackedMissing:
		return siteBlocked("recovery_infrastructure", string(reason))
	case policy.RecoveryNoWork:
		return BuildingReasonNoDeficit
	default:
		return waitFor(WaitMethodUsed, "recovery_service_proposal")
	}
}

// shelterCombatants is the squad's draft set for sheltering: the
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

// Ordinary service work does not delay a protective area correction. Only
// already-open area orders can cover that correction during an urgent review.
func recoveryWorkBlocksProtection(progress []domain.Progress, immediate bool) bool {
	if !immediate {
		return domain.StandardWorkOpen(progress)
	}
	for _, p := range progress {
		a := p.Action()
		work, assignment := a.WorkAssignment()
		animal, husbandry := a.Husbandry()
		if (assignment && work.HasArea() || husbandry && animal.Method() == domain.HusbandryAllowedArea) && domain.StandardWorkOpen([]domain.Progress{p}) {
			return true
		}
	}
	return false
}
