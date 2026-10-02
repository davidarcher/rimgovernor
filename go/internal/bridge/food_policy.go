package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// foodPolicyAction is the FoodPolicyIntent of one per-pawn food
// policy (#1541): the label and the allowed food definitions. Native
// checks the definitions are foods when it applies (NativeFoodPolicy.cs).
func foodPolicyAction(action domain.Action) (*op.Action, error) {
	v, ok := action.FoodPolicy()
	if !ok {
		return nil, contract("not a food policy action")
	}
	if _, err := domain.NewFoodPolicyAction(action.ID(), v); err != nil {
		return nil, contract("%v", err)
	}
	return &op.Action{Intent: &op.Action_FoodPolicy{FoodPolicy: &op.FoodPolicyIntent{Name: proto.String(v.Name()), AllowedDefs: v.Definitions()}}}, nil
}
