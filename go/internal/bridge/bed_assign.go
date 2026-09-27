package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// bedAssignAction is the BedAssignIntent of one bed ownership transfer: the
// pawn, the bed and the bed the pawn owned when the planner chose (or
// none). Native checks the pawn, the bed and that previous ownership live
// when it applies; a pawn that already owns the bed applies again.
func bedAssignAction(action domain.Action) (*o.Action, error) {
	v, ok := action.BedAssign()
	if !ok {
		return nil, contract("not a bed assign action")
	}
	previous := &o.Assignment{Value: &o.Assignment_Clear{Clear: &o.Clear{}}}
	if !v.PreviousBed().Clear() {
		previous = &o.Assignment{Value: &o.Assignment_EntityId{EntityId: v.PreviousBed().ID()}}
	}
	return &o.Action{Intent: &o.Action_BedAssign{BedAssign: &o.BedAssignIntent{
		PawnId: proto.String(string(v.Pawn())), BedId: proto.String(v.Bed()), ExpectedPreviousBed: previous}}}, nil
}
