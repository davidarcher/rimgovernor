package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// apparelPolicyAction is the ApparelPolicyIntent of a role apparel policy:
// the pawn and the complete filter. Native validates the pawn and the
// definitions live when it applies.
func apparelPolicyAction(action domain.Action) (*o.Action, error) {
	v, ok := action.ApparelPolicy()
	if !ok {
		return nil, contract("not an apparel policy action")
	}
	s := v.Spec()
	return &o.Action{Intent: &o.Action_ApparelPolicy{ApparelPolicy: &o.ApparelPolicyIntent{PawnId: proto.String(string(s.Pawn)), Name: proto.String(s.Name), AllowedDefs: s.Definitions, MinHitPoints: proto.Float32(float32(s.MinHP)), MaxHitPoints: proto.Float32(float32(s.MaxHP)), MinQuality: proto.Int32(s.MinQuality), MaxQuality: proto.Int32(s.MaxQuality)}}}, nil
}
