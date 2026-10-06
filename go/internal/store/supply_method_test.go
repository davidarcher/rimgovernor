package store

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
func TestSupplyMethodRequiresCensusCohortAndBoundedBatch(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"valid", "later", "moved", "oversized", "other-goal", "manual"} {
		t.Run(kind, func(t *testing.T) {
			s := open(t, memoryPath(t))
			request := roundsRequest()
			cell := domain.Cell{X: 1, Z: 2}
			lootExtentFacts(t, &request.Facts, cell)
			census := func(rows []policy.StartingSupply) domain.Fact[[]policy.LootItem] {
				var loot []policy.LootItem
				for _, row := range rows {
					loot = append(loot, policy.LootItem{Supply: row, Forbidden: true, SafeToHaul: true, SafetyKnown: true})
				}
				return domain.Known(loot)
			}
			request.Facts.EventLoot = census(supplyCohort(9, cell))
			if kind == "later" {
				request.Facts.EventLoot = census([]policy.StartingSupply{{Thing: "later", Definition: "Steel", Cell: cell}})
			}
			review := reviewRounds(t, s, &request)
			var goal WorkOwner = roundsGoal(t, review, policy.ManageSupplySafety)
			if kind == "manual" {
				request.Enabled = false
				reviewRounds(t, s, &request)
			}
			if kind == "other-goal" {
				goal = roundsGoal(t, review, policy.MaintainResource)
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
			err := s.CommitOwnerMethod(context.Background(), goal, "allow", "", supplyPlan(t, "supplies", count, cell))
			if (err == nil) != (kind == "valid") {
				t.Fatal(kind, err)
			}
		})
	}
}
