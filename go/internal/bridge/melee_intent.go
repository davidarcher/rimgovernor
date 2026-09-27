package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// meleeAction is the MeleeIntent of a melee attack or subdue: the attacker
// and target by id; native validates both live when it applies.
func meleeAction(action domain.Action) (*o.Action, error) {
	m, ok := action.MeleeAttack()
	if !ok {
		return nil, contract("not a melee action")
	}
	if validID(string(m.Pawn())) != nil || validID(string(m.Target())) != nil || m.Pawn() == m.Target() {
		return nil, contract("melee requires distinct pawn and target ids")
	}
	return &o.Action{Intent: &o.Action_Melee{Melee: &o.MeleeIntent{PawnId: proto.String(string(m.Pawn())), TargetId: proto.String(string(m.Target())), Subdue: proto.Bool(m.Subdue())}}}, nil
}
