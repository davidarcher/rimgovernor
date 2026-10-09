package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// wallRemovalAction is the wall_upgrade-guarded Deconstruct Designate of one
// stone-shell wall removal: the wall's cell, and for the
// original demolition the wall identity the proposal observed. Native
// resolves the site live.
func wallRemovalAction(action domain.Action) (*o.Action, error) {
	w, ok := action.WallRemoval()
	if !ok {
		return nil, contract("not a wall removal action")
	}
	cell := w.Cell()
	intent := &o.DesignateIntent{
		Designation: o.ThingDesignation_THING_DESIGNATION_DECONSTRUCT.Enum(),
		Cell:        &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)},
		Guard:       o.DesignationGuard_DESIGNATION_GUARD_WALL_UPGRADE.Enum(),
	}
	if w.Original() != "" {
		intent.Target = NewRef(w.Original())
	}
	return &o.Action{Intent: &o.Action_Designate{Designate: intent}}, nil
}
