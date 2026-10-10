package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// designate wraps one DesignateIntent as an Actions/Apply action.
func designate(intent *o.DesignateIntent) *o.Action {
	return &o.Action{Intent: &o.Action_Designate{Designate: intent}}
}

// deconstructAction is the DECONSTRUCT Designate of one exact building under
// the enclosure guard. Native checks the target's safety and the game
// designator live when it applies; a designation already standing applies
// again, and revoking authority releases every guarded designation natively.
func deconstructAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Deconstruction()
	if !ok {
		return nil, contract("not a deconstruction action")
	}
	if validID(v.Target()) != nil {
		return nil, contract("deconstruction target invalid")
	}
	intent := &o.DesignateIntent{Designation: o.ThingDesignation_THING_DESIGNATION_DECONSTRUCT.Enum(), Target: NewRef(v.Target()),
		Guard: o.DesignationGuard_DESIGNATION_GUARD_ENCLOSURE.Enum()}
	if v.ReplacesWithWall() {
		intent.ReplaceWithWall = proto.Bool(true)
	}
	return designate(intent), nil
}
