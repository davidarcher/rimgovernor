package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// areaPlantCutAction is the AreaPlantCutIntent over canonical cells (#1547).
// Native designates the cells' non-crop plants live (NativeAreaPlantCut.cs).
func areaPlantCutAction(action domain.Action) (*op.Action, error) {
	v, ok := action.AreaPlantCut()
	if !ok {
		return nil, contract("not an area plant cut action")
	}
	if _, err := domain.NewAreaPlantCutAction(action.ID(), v); err != nil {
		return nil, contract("%v", err)
	}
	intent := &op.AreaPlantCutIntent{}
	for _, cell := range v.Cells() {
		intent.Cells = append(intent.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	return &op.Action{Intent: &op.Action_AreaPlantCut{AreaPlantCut: intent}}, nil
}
