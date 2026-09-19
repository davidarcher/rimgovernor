package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func areaMethodPrefix(c policy.AllowedAreaChange) string {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%t/%s", c.Pawn, c.Animal, c.Area)))
	return fmt.Sprintf("area-%x-", digest[:12])
}

// Attempt identity is separate from desired state: returning to a previously
// corrected restriction must not exhaust a desired-state hash. Rotating
// candidates also lets native admission refuse an unreachable refuge without
// starving another reachable refuge or pawn.
func nextAreaChange(changes []policy.AllowedAreaChange, admitted int) (policy.AllowedAreaChange, domain.MethodID) {
	change := changes[admitted%len(changes)]
	return change, domain.MethodID(fmt.Sprintf("%s%d", areaMethodPrefix(change), admitted))
}

func (r *RoutineRecoveryPlanner) commitAreaChange(call, epoch context.Context, arbiter *stepArbiter, snapshot domain.GenerationSnapshot, goal store.GoalState, changes []policy.AllowedAreaChange, workers []policy.WorkPawn, started time.Time) (RoutineRecoveryResult, error) {
	change, method := nextAreaChange(changes, goal.Admitted)
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(change.Pawn)}) {
		return RoutineRecoveryResult{Reason: BuildingMethodUsed}, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-recovery-%x", digest[:16]))
	actionID := domain.ActionID(fmt.Sprintf("%s-0", id))
	var action domain.Action
	var err error
	if change.Animal {
		var husbandry domain.Husbandry
		husbandry, err = domain.NewHusbandry(domain.PawnID(change.Pawn), domain.HusbandryAllowedArea, change.Area)
		if err == nil {
			action, err = domain.NewHusbandryAction(actionID, husbandry)
		}
	} else {
		var token string
		for _, worker := range workers {
			if worker.ID == change.Pawn {
				token, _ = worker.SnapshotToken.Value()
			}
		}
		if token == "" {
			return RoutineRecoveryResult{Reason: BuildingMethodUnknown}, nil
		}
		var assignment domain.WorkAssignment
		assignment, err = domain.NewAreaAssignment(domain.PawnID(change.Pawn), token, change.Area == "", change.Area)
		if err == nil {
			action, err = domain.NewWorkAssignmentAction(actionID, assignment)
		}
	}
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	p := r.reviewer.player
	if err = p.current(call, epoch); err != nil {
		return RoutineRecoveryResult{}, err
	}
	state := p.session.State()
	elapsed := r.reviewer.clock.Now().Sub(started)
	if !state.Enabled || state.Snapshot != snapshot || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineRecoveryResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineRecoveryResult{}, err
	}
	return RoutineRecoveryResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

func cancelStaleAreaActions(ctx context.Context, journal *store.Store, plan store.PlanState, changes []policy.AllowedAreaChange, workers []policy.WorkPawn) error {
	for i, action := range plan.Spec.Actions() {
		stage := plan.Progress[i].View().Stage
		if stage != domain.Pending && stage != domain.Prepared {
			continue
		}
		var candidate policy.AllowedAreaChange
		if work, ok := action.WorkAssignment(); ok && work.HasArea() {
			candidate = policy.AllowedAreaChange{Pawn: policy.PawnID(work.Pawn()), Area: work.Area()}
			current := false
			for _, worker := range workers {
				token, known := worker.SnapshotToken.Value()
				if worker.ID == candidate.Pawn && known && token == work.BeforeToken() {
					current = true
				}
			}
			if !current {
				candidate.Pawn = ""
			}
		} else if husbandry, ok := action.Husbandry(); ok && husbandry.Method() == domain.HusbandryAllowedArea {
			candidate = policy.AllowedAreaChange{Pawn: policy.PawnID(husbandry.Animal()), Animal: true, Area: husbandry.Argument()}
		} else {
			continue
		}
		valid := false
		for _, change := range changes {
			if candidate == change {
				valid = true
				break
			}
		}
		if !valid {
			if _, err := journal.Cancel(ctx, plan.Spec.ID(), action.ID()); err != nil {
				return err
			}
		}
	}
	return nil
}
