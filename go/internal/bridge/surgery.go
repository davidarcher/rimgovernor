package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// surgeryAction is the medical ProductionBillIntent of one patient, one recipe and one
// body part. Native re-checks the patient, the recipe, the part and
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
	intent := &o.ProductionBillIntent{Patient: &c.Ref{Id: proto.String(string(v.Pawn()))}, RecipeDef: proto.String(v.Recipe()), AcknowledgeViolation: proto.Bool(v.AcknowledgeViolation())}
	if v.Part() != domain.NoSurgeryPart {
		intent.PartIndex = proto.Int32(int32(v.Part()))
	}
	if v.Surgeon() != "" {
		intent.Surgeon = &c.Ref{Id: proto.String(string(v.Surgeon()))}
	}
	return &o.Action{Intent: &o.Action_ProductionBill{ProductionBill: intent}}, nil
}
