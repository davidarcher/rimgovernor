package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
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
// Cleared ground rides along for the enclosure guard's roof-wait rule.
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
	for _, r := range v.ClearedGround() {
		intent.ClearedGround = append(intent.ClearedGround, &o.Rectangle{Origin: &c.Cell{X: proto.Int32(r.Origin.X), Z: proto.Int32(r.Origin.Z)}, Width: proto.Int32(r.Width), Height: proto.Int32(r.Height)})
	}
	if v.ReplacesWithWall() {
		intent.ReplaceWithWall = proto.Bool(true)
	}
	return designate(intent), nil
}
