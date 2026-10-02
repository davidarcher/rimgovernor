package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// coverDesignations maps the census's cover designation to the Designate
// designation and guard that removes the thing (#581, #1350).
var coverDesignations = map[string]struct {
	designation o.ThingDesignation
	guard       o.DesignationGuard
}{
	"Mine":        {o.ThingDesignation_THING_DESIGNATION_MINE, o.DesignationGuard_DESIGNATION_GUARD_MINE_SAFETY},
	"CutPlant":    {o.ThingDesignation_THING_DESIGNATION_CUT_PLANT, o.DesignationGuard_DESIGNATION_GUARD_UNSPECIFIED},
	"Haul":        {o.ThingDesignation_THING_DESIGNATION_HAUL, o.DesignationGuard_DESIGNATION_GUARD_UNSPECIFIED},
	"Deconstruct": {o.ThingDesignation_THING_DESIGNATION_DECONSTRUCT, o.DesignationGuard_DESIGNATION_GUARD_ENCLOSURE},
}

// coverAction is the Designate of one raider-cover clearance (#581): the
// designation that removes the exact cover thing the defense census named
// (DefenseCell.Cover) standing at its cell. Native checks the thing and the
// guard live when it applies; one already standing applies again.
func coverAction(action domain.Action) (*o.Action, error) {
	v, ok := action.CoverClearance()
	if !ok {
		return nil, contract("not a cover clearance action")
	}
	kind, known := coverDesignations[v.Designation()]
	if validID(v.Thing()) != nil || !known {
		return nil, contract("cover clearance target invalid")
	}
	intent := &o.DesignateIntent{Designation: kind.designation.Enum(), Target: NewRef(v.Thing()),
		Cell: &c.Cell{X: proto.Int32(v.Cell().X), Z: proto.Int32(v.Cell().Z)}}
	if kind.guard != o.DesignationGuard_DESIGNATION_GUARD_UNSPECIFIED {
		intent.Guard = kind.guard.Enum()
	}
	return designate(intent), nil
}
