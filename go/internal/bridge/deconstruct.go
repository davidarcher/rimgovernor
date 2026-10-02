package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// deconstructAction is the DeconstructIntent of one exact building. Native
// checks the target's safety and the game designator live when it applies;
// a target the controller already owns applies again. Revoking authority
// releases every owned designation natively. Cleared ground (#1366) rides
// along for the native enclosure and roof-wait rule.
func deconstructAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Deconstruction()
	if !ok {
		return nil, contract("not a deconstruction action")
	}
	if validID(v.Target()) != nil {
		return nil, contract("deconstruction target invalid")
	}
	intent := &o.DeconstructIntent{TargetId: proto.String(v.Target())}
	for _, r := range v.ClearedGround() {
		intent.ClearedGround = append(intent.ClearedGround, &o.Rectangle{Origin: &c.Cell{X: proto.Int32(r.Origin.X), Z: proto.Int32(r.Origin.Z)}, Width: proto.Int32(r.Width), Height: proto.Int32(r.Height)})
	}
	return &o.Action{Intent: &o.Action_Deconstruct{Deconstruct: intent}}, nil
}
