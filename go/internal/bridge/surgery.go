package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// surgeryAction is the SurgeryIntent of one patient, one recipe and one
// body part (#1162). Native re-checks the patient, the recipe, the part and
// a violation's acknowledgment live, and queues the vanilla Bill_Medical;
// the same recipe and part already queued applies again (NativeSurgery.cs).
func surgeryAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Surgery()
	if !ok {
		return nil, contract("not a surgery action")
	}
	if _, err := domain.NewSurgery(v.Pawn(), v.Recipe(), v.Part(), v.AcknowledgeViolation()); err != nil {
		return nil, contract("%v", err)
	}
	intent := &o.SurgeryIntent{PawnId: proto.String(string(v.Pawn())), RecipeDef: proto.String(v.Recipe()), AcknowledgeViolation: proto.Bool(v.AcknowledgeViolation())}
	if v.Part() != domain.NoSurgeryPart {
		intent.PartIndex = proto.Int32(int32(v.Part()))
	}
	if v.Surgeon() != "" {
		intent.SurgeonId = proto.String(string(v.Surgeon()))
	}
	return &o.Action{Intent: &o.Action_Surgery{Surgery: intent}}, nil
}
