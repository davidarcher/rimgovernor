package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (r *RoutineMedicalPlanner) planMedicineTier(call, epoch context.Context, state ControlState, review store.RoutineReview, arbiter *stepArbiter) (RoutineMedicalResult, error) {
	p := r.reviewer.player
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainMedicalCare {
			var err error
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			if err != nil {
				return RoutineMedicalResult{}, err
			}
			break
		}
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineMedicalResult{}, nil
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil || !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineMedicalResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	work := medicineTierAssignments(read.Projection.Facts)
	// Only this method's pending care writes are superseded. Hospital and bed
	// rest methods share the goal and continue independently.
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
		for _, progress := range plan.Progress {
			old, ok := progress.Action().WorkAssignment()
			if !ok || old.MedicalCare() == "" || !domain.GoalWorkOpen([]domain.Progress{progress}) {
				continue
			}
			wanted := false
			for _, fresh := range work {
				wanted = wanted || old == fresh
			}
			stage := progress.View().Stage
			if !wanted && (stage == domain.Pending || stage == domain.Prepared) {
				if _, err := p.journal.Cancel(call, method.Plan, progress.Action().ID()); err != nil {
					return RoutineMedicalResult{}, err
				}
			} else {
				arbiter.tryClaim([]domain.PawnID{old.Pawn()})
				return RoutineMedicalResult{Reason: BuildingMethodExistingWork}, nil
			}
		}
	}
	for _, w := range work {
		if !arbiter.tryClaim([]domain.PawnID{w.Pawn()}) {
			continue
		}
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%d/%s/%s/%s", goal.Goal.ID, goal.Goal.Epoch, expected.Tick, w.Pawn(), w.BeforeToken(), w.MedicalCare())))
		method := domain.MethodID(fmt.Sprintf("medicine-tier-%x", digest[:16]))
		for _, previous := range goal.Methods {
			if previous.Method == method {
				return RoutineMedicalResult{}, nil
			}
		}
		id := domain.PlanID(method)
		action, err := domain.NewWorkAssignmentAction(domain.ActionID(string(id)+"-0"), w)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return RoutineMedicalResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoutineMedicalResult{}, err
		}
		if p.session.State() != state {
			return RoutineMedicalResult{}, ErrControl
		}
		if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
			return RoutineMedicalResult{}, err
		}
		return RoutineMedicalResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
	}
	return RoutineMedicalResult{}, nil
}

func medicineTierAssignments(f policy.RoutineFacts) []domain.WorkAssignment {
	pawns, known := f.MedicalPawns.Value()
	if !known {
		return nil
	}
	var work []domain.WorkAssignment
	for _, pawn := range pawns {
		dead, dk := pawn.Dead.Value()
		care, ck := pawn.Care.Value()
		token, tk := pawn.SettingsToken.Value()
		if !dk || dead || !ck || !tk {
			continue
		}
		tier, known := policy.SelectMedicineTier(pawn.Conditions, pawn.LifeThreatening, f.Resources).Value()
		if !known || string(tier) == care {
			continue
		}
		w, err := domain.NewMedicalCareAssignment(domain.PawnID(pawn.ID), token, string(tier))
		if err == nil {
			work = append(work, w)
		}
	}
	return work
}
