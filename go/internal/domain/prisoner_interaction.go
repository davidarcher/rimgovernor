package domain

import "errors"

// PrisonerInteractionMode names Population-*'s direct-write prisoner custody
// order: one exclusive native interaction. Recruit and MaintainOnly are the
// routine pair; ReduceResistance and Release are Core modes and Enslave and
// Convert exist only while Ideology is active (native refuses them
// otherwise). Execution and the non-exclusive toggles stay unsupported at
// this boundary. Routine planning only ever proposes Recruit (see
// policy.MaintainPopulation); the other modes are explicit orders.
type PrisonerInteractionMode string

const (
	PrisonerInteractionRecruit          PrisonerInteractionMode = "recruit"
	PrisonerInteractionMaintain         PrisonerInteractionMode = "maintain"
	PrisonerInteractionReduceResistance PrisonerInteractionMode = "reduce_resistance"
	PrisonerInteractionRelease          PrisonerInteractionMode = "release"
	PrisonerInteractionEnslave          PrisonerInteractionMode = "enslave"
	PrisonerInteractionConvert          PrisonerInteractionMode = "convert"
)

// PrisonerInteractionModes lists every supported mode in wire order.
var PrisonerInteractionModes = []PrisonerInteractionMode{
	PrisonerInteractionRecruit, PrisonerInteractionMaintain, PrisonerInteractionReduceResistance,
	PrisonerInteractionRelease, PrisonerInteractionEnslave, PrisonerInteractionConvert,
}

// PrisonerInteraction is explicit intent to write one already-observed
// prisoner's exclusive interaction mode. Native eligibility (recruitable,
// alive, not a wild man barred from the mode) is established at inspection,
// not here -- the same split HusbandryTrain/HusbandrySlaughter use.
type PrisonerInteraction struct {
	pawn        PawnID
	interaction PrisonerInteractionMode
}

func NewPrisonerInteraction(pawn PawnID, interaction PrisonerInteractionMode) (PrisonerInteraction, error) {
	if !validID(string(pawn)) {
		return PrisonerInteraction{}, errors.New("prisoner interaction requires a valid pawn identity")
	}
	switch interaction {
	case PrisonerInteractionRecruit, PrisonerInteractionMaintain, PrisonerInteractionReduceResistance,
		PrisonerInteractionRelease, PrisonerInteractionEnslave, PrisonerInteractionConvert:
	default:
		return PrisonerInteraction{}, errors.New("invalid prisoner interaction mode")
	}
	return PrisonerInteraction{pawn: pawn, interaction: interaction}, nil
}

func (p PrisonerInteraction) Pawn() PawnID                         { return p.pawn }
func (p PrisonerInteraction) Interaction() PrisonerInteractionMode { return p.interaction }

const PrisonerInteractionAction ActionKind = "prisoner_interaction"

func NewPrisonerInteractionAction(id ActionID, interaction PrisonerInteraction) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewPrisonerInteraction(interaction.pawn, interaction.interaction); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: PrisonerInteractionAction, prisonerInteraction: interaction}, nil
}

func (a Action) PrisonerInteraction() (PrisonerInteraction, bool) {
	return a.prisonerInteraction, a.kind == PrisonerInteractionAction
}
