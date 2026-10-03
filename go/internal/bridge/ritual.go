package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// ritualAction is the RitualIntent of one ritual command (#1639): the
// colonist the ritual is for, the ritual and the verb; native checks the
// ritual waits for the command when it applies.
func ritualAction(action domain.Action) (*o.Action, error) {
	ritual, ok := action.Ritual()
	if !ok {
		return nil, contract("not a ritual action")
	}
	if _, err := domain.NewRitual(ritual.Pawn(), ritual.Ritual(), ritual.Verb()); err != nil {
		return nil, contract("ritual requires a pawn, a known ritual and a known verb")
	}
	return &o.Action{Intent: &o.Action_Ritual{Ritual: &o.RitualIntent{
		PawnId: proto.String(string(ritual.Pawn())), Ritual: proto.String(string(ritual.Ritual())), Verb: proto.String(string(ritual.Verb())),
	}}}, nil
}
