package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// removeRoofAction is the RemoveRoofIntent over canonical cells.
// Native designates vanilla RemoveRoof live (NativeRemoveRoof.cs).
func removeRoofAction(action domain.Action) (*op.Action, error) {
	v, ok := action.RemoveRoof()
	if !ok {
		return nil, contract("not a remove roof action")
	}
	if _, err := domain.NewRemoveRoofAction(action.ID(), v); err != nil {
		return nil, contract("%v", err)
	}
	intent := &op.RemoveRoofIntent{}
	for _, cell := range v.Cells() {
		intent.Cells = append(intent.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	return &op.Action{Intent: &op.Action_RemoveRoof{RemoveRoof: intent}}, nil
}
