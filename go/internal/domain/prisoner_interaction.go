package domain

import "errors"

// PrisonerInteractionMode names Population-*'s direct-write prisoner custody
// order: the normal Recruit or MaintainOnly exclusive interaction, mirroring
// the native PrisonerInteractionModeDefOf.AttemptRecruit/MaintainOnly pair
// PopulationTool.cs's legacy home/population write already exposed. Other
// interactions are unsupported at this boundary.
type PrisonerInteractionMode string

const (
	PrisonerInteractionRecruit  PrisonerInteractionMode = "recruit"
	PrisonerInteractionMaintain PrisonerInteractionMode = "maintain"
)

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
	case PrisonerInteractionRecruit, PrisonerInteractionMaintain:
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
