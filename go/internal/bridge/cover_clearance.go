package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// coverAction is the CoverIntent of one raider-cover clearance (#581): the
// designation that removes the exact cover thing the defense census named
// (DefenseCell.Cover). Native checks the thing and the designation live when
// it applies; one already standing applies again.
func coverAction(action domain.Action) (*o.Action, error) {
	v, ok := action.CoverClearance()
	if !ok {
		return nil, contract("not a cover clearance action")
	}
	if validID(v.Thing()) != nil || validID(v.Designation()) != nil {
		return nil, contract("cover clearance target invalid")
	}
	return &o.Action{Intent: &o.Action_Cover{Cover: &o.CoverIntent{ThingId: proto.String(v.Thing()), DesignationDef: proto.String(v.Designation()),
		Cell: &c.Cell{X: proto.Int32(v.Cell().X), Z: proto.Int32(v.Cell().Z)}}}}, nil
}
