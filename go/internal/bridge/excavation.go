package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// excavateAction is the ExcavateIntent of one rock cell. Native checks the
// rock, roof support and the game designator live when it applies, adopting
// a standing Mine designation; applied means designated, and the site read
// decides when the cell is cleared.
func excavateAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Excavation()
	if !ok {
		return nil, contract("not an excavation action")
	}
	if _, err := domain.NewExcavation(v.Cell(), v.Definition()); err != nil {
		return nil, err
	}
	cell := v.Cell()
	return &o.Action{Intent: &o.Action_Excavate{Excavate: &o.ExcavateIntent{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, ExpectedMineableDefName: proto.String(v.Definition())}}}, nil
}
