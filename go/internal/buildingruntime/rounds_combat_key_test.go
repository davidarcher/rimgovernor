package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

type combatKeySource struct {
	RoundsDefenseSource
	fake *combatOrdersFake
}

func (s combatKeySource) CombatOrders(ctx context.Context, id *c.Identity, key string, command *op.CombatOrders) ([]bridge.CombatOrderResult, error) {
	return s.fake.CombatOrders(ctx, id, key, command)
}

// Two different batches at one tick must not share an idempotency key (native
// would replay the first receipt, #2344); an identical batch reuses it.
func TestCombatBatchKeyPerBatch(t *testing.T) {
	fake := &combatOrdersFake{}
	planner := &RoundsDefensePlanner{native: combatKeySource{fake: fake}}
	send := func(pawns ...domain.PawnID) {
		var orders []policy.CombatOrder
		for _, p := range pawns {
			orders = append(orders, policy.CombatOrder{Pawn: p, Kind: policy.OrderMove, Cell: domain.Cell{X: 1, Z: 2}})
		}
		results, _, err := planner.sendCombatBatch(context.Background(), ControlState{}, "plan-stop-183", nil, orders)
		if err != nil || len(results) != len(orders) {
			t.Fatalf("results %d err %v", len(results), err)
		}
	}
	send("a", "b", "c", "d", "e", "f")
	send("a", "b", "c")
	send("a", "b", "c")
	if len(fake.keys) != 3 || fake.keys[0] == fake.keys[1] || fake.keys[1] != fake.keys[2] {
		t.Fatalf("keys %v", fake.keys)
	}
}
