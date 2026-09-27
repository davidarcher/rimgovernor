package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// combatOrdersFake applies every combat order and keeps each batch.
type combatOrdersFake struct {
	batches []*op.CombatOrders
	refuse  map[string]string // pawn -> refusal
	asks    []*mp.CombatGeometryRequest
	propose []domain.Cell // the game's covered cells behind the line
}

func (f *combatOrdersFake) CombatOrders(ctx context.Context, _ *a.WritePrecondition, command *op.CombatOrders) ([]bridge.CombatOrderResult, *op.ExecuteReply, bridge.Result, error) {
	if err := bridge.ValidateCombatOrders(command); err != nil {
		return nil, nil, bridge.Result{}, err
	}
	f.batches = append(f.batches, command)
	out := make([]bridge.CombatOrderResult, len(command.Orders))
	for i, order := range command.Orders {
		pawn := order.GetPawn().GetEntityId()
		out[i] = bridge.CombatOrderResult{Index: i, PawnID: pawn, Applied: true}
		if reason := f.refuse[pawn]; reason != "" {
			out[i].Applied, out[i].Refusal = false, reason
		}
	}
	return out, nil, bridge.Result{}, ctx.Err()
}

func (n *equipTestNative) CombatOrders(ctx context.Context, pre *a.WritePrecondition, command *op.CombatOrders) ([]bridge.CombatOrderResult, *op.ExecuteReply, bridge.Result, error) {
	return n.orders.CombatOrders(ctx, pre, command)
}

func (n *defenseReplayNative) CombatOrders(ctx context.Context, pre *a.WritePrecondition, command *op.CombatOrders) ([]bridge.CombatOrderResult, *op.ExecuteReply, bridge.Result, error) {
	return n.orders.CombatOrders(ctx, pre, command)
}

// CombatGeometry validates the ask and proposes the fake's cells.
func (f *combatOrdersFake) CombatGeometry(ctx context.Context, request *mp.CombatGeometryRequest) (*mp.CombatGeometry, bridge.Result, error) {
	f.asks = append(f.asks, request)
	if err := bridge.ValidateCombatGeometryRequest(request); err != nil {
		return nil, bridge.Result{}, err
	}
	g := &mp.CombatGeometry{}
	for _, cell := range f.propose {
		g.Proposed = append(g.Proposed, &mp.CombatGeometryCell{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, Standable: proto.Bool(true)})
	}
	return g, bridge.Result{}, ctx.Err()
}

func (n *equipTestNative) CombatGeometry(ctx context.Context, request *mp.CombatGeometryRequest) (*mp.CombatGeometry, bridge.Result, error) {
	return n.orders.CombatGeometry(ctx, request)
}

func (n *defenseReplayNative) CombatGeometry(ctx context.Context, request *mp.CombatGeometryRequest) (*mp.CombatGeometry, bridge.Result, error) {
	return n.orders.CombatGeometry(ctx, request)
}
