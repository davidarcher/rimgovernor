package store

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
func supplyCohort(count int, cell domain.Cell) []policy.StartingSupply {
	var rows []policy.StartingSupply
	for i := 0; i < count; i++ {
		rows = append(rows, policy.StartingSupply{Thing: fmt.Sprintf("item-%d", i), Definition: "Steel", Cell: cell})
	}
	return rows
}
func TestSupplyMethodRequiresOriginalCohortAndBoundedBatch(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"valid", "later", "moved", "oversized", "other-goal", "manual"} {
		t.Run(kind, func(t *testing.T) {
			s := open(t, memoryPath(t))
			request := routineRequest()
			cell := domain.Cell{X: 1, Z: 2}
			request.Facts.StartingSupplies = domain.Known(supplyCohort(9, cell))
			if kind == "later" {
				request.Facts.StartingSupplies = domain.Known([]policy.StartingSupply{{Thing: "later", Definition: "Steel", Cell: cell}})
			}
			review := reviewRoutine(t, s, &request)
			goal := routineGoal(t, review, policy.AllowStartingSupplies)
			if kind == "manual" {
				request.Enabled = false
				reviewRoutine(t, s, &request)
			}
			if kind == "other-goal" {
				goal = routineGoal(t, review, policy.MaintainWood)
			}
			count := 1
			if kind == "moved" {
				// The census cell is where the planner read the stack; a plan
				// naming it elsewhere was composed from a stale read.
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
			if err != nil || len(claims) != 0 {
				t.Fatal("admission alone claimed", claims, err)
			}
		})
	}
}

// A completed Allow claims its item for the rest of the world so a stack the
// player re-forbids is never allowed twice; a cancelled attempt (the stack
// left its cell first) claims nothing, so the next census re-targets it.
func TestSupplyClaimFollowsCompletedAllowNotCancelledAttempt(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	request := routineRequest()
	request.Current.Native = 1
	cell := domain.Cell{X: 1, Z: 2}
	request.Facts.StartingSupplies = domain.Known(supplyCohort(2, cell))
	review := reviewRoutine(t, s, &request)
	goal := routineGoal(t, review, policy.AllowStartingSupplies)
	world := World{Colony: request.Current.Colony, Load: request.Current.Load, Map: request.Current.Map}
	if _, err := s.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, "allow", supplyPlan(t, "first", 2, cell)); err != nil {
		t.Fatal(err)
	}
	plan := supplyPlan(t, "first", 2, cell)
	snapshot := request.Current
	snapshot.Plan, snapshot.Revision = plan.ID(), plan.Revision()
	// item-0 is allowed; item-1 was hauled aside before its write.
	first := plan.Actions()[0]
	var err error
	if _, err = s.PrepareSupply(ctx, plan.ID(), first.ID(), SupplyAdmission{Snapshot: snapshot, Tick: 10, Thing: "item-0", SnapshotToken: "cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, plan.ID(), first.ID(), snapshot, 10); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Observe(ctx, plan.ID(), domain.Observation{Action: first.ID(), Attempt: 1, Snapshot: snapshot, Tick: 11, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}, snapshot); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Cancel(ctx, plan.ID(), plan.Actions()[1].ID()); err != nil {
		t.Fatal(err)
	}
	claims, err := s.SupplyClaims(ctx, world)
	if err != nil || len(claims) != 1 || !claims["item-0"] {
		t.Fatal(claims, err)
	}
	s.Close()
	s = open(t, path)
	request.Current.Native++
	review = reviewRoutine(t, s, &request)
	goal = routineGoal(t, review, policy.AllowStartingSupplies)
	if claims, err = s.SupplyClaims(ctx, world); err != nil || len(claims) != 1 || !claims["item-0"] {
		t.Fatal("claim lost across restart and direction change", claims, err)
	}
	if claims, err = s.SupplyClaims(ctx, World{Colony: world.Colony, Load: "other", Map: world.Map}); err != nil || len(claims) != 0 {
		t.Fatal("claim crossed worlds", claims, err)
	}
	// The cancelled stack is still cohort work at the cell the census reports.
	if _, err = s.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, "allow-again", supplyPlan(t, "second", 2, cell)); err != nil {
		t.Fatal("cohort stack refused after a cancelled attempt", err)
	}
}
