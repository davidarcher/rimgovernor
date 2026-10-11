package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// buildRoofAction is the BuildRoofIntent over canonical cells.
// Native designates vanilla BuildRoof live (NativeBuildRoof.cs).
func buildRoofAction(action domain.Action) (*op.Action, error) {
	v, ok := action.BuildRoof()
	if !ok {
		return nil, contract("not a build roof action")
	}
	if _, err := domain.NewBuildRoofAction(action.ID(), v); err != nil {
		return nil, contract("%v", err)
	}
	intent := &op.BuildRoofIntent{}
	for _, cell := range v.Cells() {
		intent.Cells = append(intent.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	return &op.Action{Intent: &op.Action_BuildRoof{BuildRoof: intent}}, nil
}
