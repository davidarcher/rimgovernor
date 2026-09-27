package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// prisonerInteractionWire maps each domain mode to the exclusive interaction
// native sets (NativePrisonerInteractionOperations.cs); Enslave and Convert
// exist only while Ideology is active, and native refuses them otherwise.
var prisonerInteractionWire = map[domain.PrisonerInteractionMode]o.PrisonerInteraction{
	domain.PrisonerInteractionRecruit:          o.PrisonerInteraction_PRISONER_INTERACTION_ATTEMPT_RECRUIT,
	domain.PrisonerInteractionMaintain:         o.PrisonerInteraction_PRISONER_INTERACTION_MAINTAIN_ONLY,
	domain.PrisonerInteractionReduceResistance: o.PrisonerInteraction_PRISONER_INTERACTION_REDUCE_RESISTANCE,
	domain.PrisonerInteractionRelease:          o.PrisonerInteraction_PRISONER_INTERACTION_RELEASE,
	domain.PrisonerInteractionEnslave:          o.PrisonerInteraction_PRISONER_INTERACTION_ENSLAVE,
	domain.PrisonerInteractionConvert:          o.PrisonerInteraction_PRISONER_INTERACTION_CONVERT,
}

// prisonerInteractionAction is the PrisonerInteractionIntent of one
// prisoner custody order. Native checks the prisoner and the mode's
// eligibility live when it applies; a prisoner already set to the mode
// applies again.
func prisonerInteractionAction(action domain.Action) (*o.Action, error) {
	v, ok := action.PrisonerInteraction()
	if !ok {
		return nil, contract("not a prisoner interaction action")
	}
	wire, ok := prisonerInteractionWire[v.Interaction()]
	if !ok || validID(string(v.Pawn())) != nil {
		return nil, contract("invalid prisoner interaction")
	}
	return &o.Action{Intent: &o.Action_Prisoner{Prisoner: &o.PrisonerInteractionIntent{PawnId: proto.String(string(v.Pawn())), Interaction: &wire}}}, nil
}
