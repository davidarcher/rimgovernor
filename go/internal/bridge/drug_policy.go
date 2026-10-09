package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// drugPolicyAction is the DrugPolicyIntent of one per-pawn drug policy:
// the label and the entries that allow anything. Native checks the
// drugs are the policy's when it applies (NativeDrugPolicy.cs).
func drugPolicyAction(action domain.Action) (*op.Action, error) {
	v, ok := action.DrugPolicy()
	if !ok {
		return nil, contract("not a drug policy action")
	}
	if _, err := domain.NewDrugPolicyAction(action.ID(), v); err != nil {
		return nil, contract("%v", err)
	}
	intent := &op.DrugPolicyIntent{Name: proto.String(v.Name())}
	for _, e := range v.Entries() {
		intent.Entries = append(intent.Entries, &op.DrugPolicyEntry{
			DrugDef: proto.String(e.Drug), AllowedForJoy: proto.Bool(e.Joy), AllowedForAddiction: proto.Bool(e.Addiction),
			AllowScheduled: proto.Bool(e.Scheduled), DaysFrequency: proto.Float32(float32(e.DaysFrequency)),
			OnlyIfMoodBelow: proto.Float32(float32(e.OnlyIfMoodBelow)), OnlyIfJoyBelow: proto.Float32(float32(e.OnlyIfJoyBelow)),
			TakeToInventory: proto.Int32(int32(e.TakeToInventory)),
		})
	}
	return &op.Action{Intent: &op.Action_DrugPolicy{DrugPolicy: intent}}, nil
}
