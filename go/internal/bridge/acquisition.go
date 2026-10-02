package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// acquireAction is the acquisition-guarded Designate of an acquisition or a
// mine acquisition (designate the source), or of a stall withdraw
// (withdraw=true, #1046, #1351). The designation is the one the source
// takes: MINE for a mine acquisition, HUNT for a corpse resource, otherwise
// HARVEST_PLANT. Native checks the source live; a designation already in
// the requested state applies again, and the next census reads progress.
func acquireAction(action domain.Action) (*op.Action, error) {
	acquisition, withdraw, ok := action.AcquireIntent()
	if !ok {
		return nil, contract("not an acquisition action")
	}
	if _, err := domain.NewAcquisition(acquisition.Thing(), acquisition.Definition(), acquisition.Cell()); err != nil {
		return nil, contract("acquire intent requires a source, a resource and a cell")
	}
	designation := op.ThingDesignation_THING_DESIGNATION_HARVEST_PLANT
	switch {
	case action.Kind() == domain.MineAcquisitionAction:
		designation = op.ThingDesignation_THING_DESIGNATION_MINE
	case acquisition.Hunt():
		designation = op.ThingDesignation_THING_DESIGNATION_HUNT
	}
	intent := &op.DesignateIntent{
		Designation: designation.Enum(),
		Target:      NewRef(acquisition.Thing()),
		Cell:        &c.Cell{X: proto.Int32(acquisition.Cell().X), Z: proto.Int32(acquisition.Cell().Z)},
		Guard:       op.DesignationGuard_DESIGNATION_GUARD_ACQUISITION.Enum(),
		ExpectedDef: proto.String(acquisition.Definition()),
	}
	if withdraw {
		intent.Withdraw = proto.Bool(true)
	}
	return &op.Action{Intent: &op.Action_Designate{Designate: intent}}, nil
}
