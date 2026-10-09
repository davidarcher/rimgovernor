package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// stripAction is the DesignateIntent that places vanilla's Strip designation
// on one exact pawn or corpse. Native refuses a target that is not
// spawned, has nothing to strip, or is already designated.
func stripAction(action domain.Action) (*op.Action, error) {
	v, ok := action.Strip()
	if !ok {
		return nil, contract("not a strip action")
	}
	if validID(v.Target()) != nil {
		return nil, contract("strip target invalid")
	}
	return &op.Action{Intent: &op.Action_Designate{Designate: &op.DesignateIntent{ThingId: proto.String(v.Target()), Designation: op.ThingDesignation_THING_DESIGNATION_STRIP.Enum()}}}, nil
}
