package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// readingPolicyAction is the ReadingPolicyIntent of one per-pawn reading
// policy: the label and the allowed book definitions. Native
// checks the definitions are books when it applies (NativeReadingPolicy.cs).
func readingPolicyAction(action domain.Action) (*op.Action, error) {
	v, ok := action.ReadingPolicy()
	if !ok {
		return nil, contract("not a reading policy action")
	}
	if _, err := domain.NewReadingPolicyAction(action.ID(), v); err != nil {
		return nil, contract("%v", err)
	}
	return &op.Action{Intent: &op.Action_ReadingPolicy{ReadingPolicy: &op.ReadingPolicyIntent{Name: proto.String(v.Name()), AllowedDefs: v.Definitions()}}}, nil
}
