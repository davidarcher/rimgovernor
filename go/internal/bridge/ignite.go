package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// igniteAction is the Actions/Apply ignite arm of one ignite action:
// the pawn throws a molotov at the cell. Room occupancy is Go policy (policy.BurnOccupied).
func igniteAction(action domain.Action) (*o.Action, error) {
	ignite, ok := action.Ignite()
	if !ok {
		return nil, contract("not an ignite action")
	}
	if _, err := domain.NewIgnite(ignite.Pawn(), ignite.Cell()); err != nil {
		return nil, contract("ignite: %v", err)
	}
	return &o.Action{Intent: &o.Action_Ignite{Ignite: &o.IgniteIntent{PawnId: proto.String(string(ignite.Pawn())),
		Cell: &c.Cell{X: proto.Int32(ignite.Cell().X), Z: proto.Int32(ignite.Cell().Z)}}}}, nil
}
