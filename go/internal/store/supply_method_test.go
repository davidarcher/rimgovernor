package store

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"path/filepath"
	"testing"
)

func supplyPlan(t *testing.T, id string, count int, cell domain.Cell) domain.PlanSpec {
	t.Helper()
	var actions []domain.Action
	for i := 0; i < count; i++ {
		supply, err := domain.NewSupplyAllow(fmt.Sprintf("item-%d", i), "Steel", cell)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewSupplyAllowAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), supply)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(domain.PlanID(id), 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	return plan
}
func TestSupplyMethodRequiresOriginalCohortAndBoundedBatch(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"valid", "later", "oversized", "other-goal", "manual"} {
		t.Run(kind, func(t *testing.T) {
			s := open(t, filepath.Join(t.TempDir(), "s.db"))
			request := routineRequest()
			request.Facts.StartingSupplyCells = domain.Known([]domain.Cell{{X: 1, Z: 2}})

			review := reviewRoutine(t, s, &request)
			goal := routineGoal(t, review, policy.AllowStartingSupplies)
			if kind == "manual" {
				request.Enabled = false
				reviewRoutine(t, s, &request)
			}
			if kind == "other-goal" {
				goal = routineGoal(t, review, policy.MaintainWood)
			}
			cell, count := domain.Cell{X: 1, Z: 2}, 1
			if kind == "later" {
				cell.X = 3
			}
			if kind == "oversized" {
				count = 9
			}
			_, err := s.CommitGoalMethod(context.Background(), goal.Goal.ID, goal.Revision, "allow", supplyPlan(t, "supplies", count, cell))
			if (err == nil) != (kind == "valid") {
				t.Fatal(kind, err)
			}
			claims, err := s.SupplyClaims(context.Background(), World{Colony: request.Current.Colony, Load: request.Current.Load, Map: request.Current.Map})
			if err != nil {
				t.Fatal(err)
			}
			if len(claims) != map[bool]int{true: 1, false: 0}[kind == "valid"] {
				t.Fatal("non-atomic claims", claims)
			}
		})
	}
}
func TestSupplyClaimSurvivesCancellationRestartAndDirectionChange(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "s.db")
	s := open(t, path)
	request := routineRequest()
	request.Facts.StartingSupplyCells = domain.Known([]domain.Cell{{X: 1, Z: 2}})
	review := reviewRoutine(t, s, &request)
	goal := routineGoal(t, review, policy.AllowStartingSupplies)
	admitted, err := s.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, "allow", supplyPlan(t, "first", 1, domain.Cell{X: 1, Z: 2}))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CancelGoal(ctx, admitted.Goal.ID, admitted.Revision); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	request.Current.Direction++
	review = reviewRoutine(t, s, &request)
	goal = routineGoal(t, review, policy.AllowStartingSupplies)
	if _, err = s.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, "allow-again", supplyPlan(t, "second", 1, domain.Cell{X: 1, Z: 2})); err == nil {
		t.Fatal("re-admitted a claimed item")
	}
	if _, err = s.LoadPlan(ctx, "second"); err == nil {
		t.Fatal("failed method leaked plan")
	}
}
