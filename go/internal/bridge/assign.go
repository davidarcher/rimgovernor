package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// assignAction is the AssignIntent of one ownership transfer: the pawn, the
// assignable thing (a bed, a throne) and the thing of that kind the pawn
// owned when the planner chose (or none). Native checks the pawn, the thing
// and that previous ownership live when it applies; a pawn that already
// owns the thing applies again.
func assignAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Assign()
	if !ok {
		return nil, contract("not an assign action")
	}
	previous := &o.Assignment{Value: &o.Assignment_Clear{Clear: &o.Clear{}}}
	if !v.Previous().Clear() {
		previous = &o.Assignment{Value: &o.Assignment_EntityId{EntityId: v.Previous().ID()}}
	}
	intent := &o.AssignIntent{PawnId: proto.String(string(v.Pawn())), ThingId: proto.String(v.Thing()), ExpectedPrevious: previous}
	if v.Swap() {
		intent.Swap = proto.Bool(true)
	}
	return &o.Action{Intent: &o.Action_Assign{Assign: intent}}, nil
}
