package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestCombatBatchKeyPerBatch(t *testing.T) {
	makePlan := func(pawns ...domain.PawnID) domain.PlanSpec {
		var orders []policy.CombatOrder
		for _, p := range pawns {
			orders = append(orders, policy.CombatOrder{Pawn: p, Kind: policy.OrderMove, Cell: domain.Cell{X: 1, Z: 2}})
		}
		plan, err := combatBatchPlan("fight", "plan-stop-183", nil, orders)
		if err != nil {
			t.Fatal(err)
		}
		return plan
	}
	first, second, retry := makePlan("a", "b"), makePlan("a"), makePlan("a")
	if first.ID() == second.ID() || second.ID() != retry.ID() || second.Actions()[0] != retry.Actions()[0] {
		t.Fatal("combat identity not immutable")
	}
}
