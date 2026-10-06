package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// cutPlantAction is the DesignateIntent that orders one exact blighted plant
// cut (#245). Native checks the plant live when it applies; a plant already
// designated applies again.
func cutPlantAction(action domain.Action) (*op.Action, error) {
	v, ok := action.CutPlant()
	if !ok {
		return nil, contract("not a cut plant action")
	}
	if validID(v.Plant()) != nil {
		return nil, contract("cut plant target invalid")
	}
	return &op.Action{Intent: &op.Action_Designate{Designate: &op.DesignateIntent{ThingId: proto.String(v.Plant()), Designation: op.ThingDesignation_THING_DESIGNATION_CUT_PLANT.Enum()}}}, nil
}
