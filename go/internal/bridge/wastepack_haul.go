package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// wastepackHaulAction is the DesignateIntent HAUL of one exact wastepack
// under the wastepack guard (#1683). Native checks the pack and the guard
// live when it applies; a designation already standing applies again.
func wastepackHaulAction(action domain.Action) (*o.Action, error) {
	v, ok := action.WastepackHaul()
	if !ok {
		return nil, contract("not a wastepack haul action")
	}
	if validID(v.Thing()) != nil {
		return nil, contract("wastepack haul target invalid")
	}
	return designate(&o.DesignateIntent{Designation: o.ThingDesignation_THING_DESIGNATION_HAUL.Enum(), Target: NewRef(v.Thing()),
		Cell:  &c.Cell{X: proto.Int32(v.Cell().X), Z: proto.Int32(v.Cell().Z)},
		Guard: o.DesignationGuard_DESIGNATION_GUARD_WASTEPACK.Enum(), ExpectedDef: proto.String(v.Definition())}), nil
}
