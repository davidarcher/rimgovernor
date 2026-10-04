package buildingruntime

import (
	"context"
	"fmt"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// throneRefuelSpacingTicks is one game hour: a refuel order for a light
// stands that long before another is admitted for it, so a hauler on its way
// is not stacked with more orders and a refused one is retried.
const throneRefuelSpacingTicks = 2500

func throneRefuelPrefix(lamp string) string { return "throne-refuel-" + lamp + "-" }

// throneRefuelRecent reports whether an order for lamp was admitted within
// the spacing before tick.
func throneRefuelRecent(history []domain.GoalMethod, lamp string, tick domain.Tick) bool {
	prefix := throneRefuelPrefix(lamp)
	for _, m := range history {
		if !strings.HasPrefix(string(m.Method), prefix) {
			continue
		}
		at, err := strconv.ParseInt(strings.TrimPrefix(string(m.Method), prefix), 10, 64)
		if err == nil && tick-domain.Tick(at) < throneRefuelSpacingTicks {
			return true
		}
	}
	return false
}

// refuelThrone admits one forced refuel order for an unlit, empty throne
// room light as a recovery_service action under the housing goal: the same
// native work-giver job the defense rearm issues, whose CAS token and pawn
// eligibility Hands re-check at dispatch.
func (r *RoutineSleepingUpkeepPlanner) refuelThrone(call, epoch context.Context, arbiter *stepArbiter, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoutineReading, step policy.ThroneStep) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	tick := reading.Projection.Identity.Tick
	history, err := p.journal.LoadOwnerMethods(call, goal)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if throneRefuelRecent(history, step.Lamp, tick) {
		return RoutineBuildingResult{Verdict: waitFor(WaitMethodUsed, "throne_refuel")}, nil
	}
	if arbiter == nil || !arbiter.tryClaim([]domain.PawnID{domain.PawnID(step.Pawn)}, "throne-refuel:"+step.Lamp) {
		return RoutineBuildingResult{Verdict: waitFor(WaitMethodUsed, "throne_refuel_claim")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", throneRefuelPrefix(step.Lamp), tick))
	service, err := domain.NewRecoveryService(domain.PawnID(step.Pawn), step.Lamp, domain.RecoveryServiceRefuel)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewRecoveryServiceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), service)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineBuildingResult{}, err
	}
	if p.session.State() != state {
		return RoutineBuildingResult{}, fmt.Errorf("%w: refuelThrone: p.session.State() != state", ErrControl)
	}
	latest, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if latest.Revision != review.Revision || !latest.Enabled {
		return RoutineBuildingResult{}, fmt.Errorf("%w: refuelThrone: latest.Revision != review.Revision || !latest.Enabled", ErrControl)
	}
	if err = p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoutineBuildingResult{}, err
	}
	clockSchedulerLog("%s: throne light %s refuel by %s (%s)", goal.OwnerID(), step.Lamp, step.Pawn, method)
	return RoutineBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}
