package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// deconstructAction is the DeconstructIntent of one exact building. Native
// checks the target's safety and the game designator live when it applies;
// a target the controller already owns applies again. Revoking authority
// releases every owned designation natively.
func deconstructAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Deconstruction()
	if !ok {
		return nil, contract("not a deconstruction action")
	}
	if validID(v.Target()) != nil {
		return nil, contract("deconstruction target invalid")
	}
	return &o.Action{Intent: &o.Action_Deconstruct{Deconstruct: &o.DeconstructIntent{TargetId: proto.String(v.Target())}}}, nil
}
