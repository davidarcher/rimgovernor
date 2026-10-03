package domain

import "errors"

const RitualAction ActionKind = "ritual"

// RitualKind names the ritual a Ritual command addresses; RitualVerb what it
// asks of it. Both are closed sets native resolves against the game, so a
// new ritual (an Ideology one) adds a kind here and an arm native-side, not
// an action kind.
type RitualKind string
type RitualVerb string

const (
	// RitualBestowing is the Empire's bestowing ceremony of the colonist the
	// Ritual names.
	RitualBestowing RitualKind = "bestowing"
	// RitualStart is the player's command that begins a ritual whose lord
	// waits for it (the bestower's Command_BestowerCeremony gizmo).
	RitualStart RitualVerb = "start"
)

// Ritual is a player command to one ritual of one pawn (#1639, epic #1598):
// start the ritual that is waiting for the command. Pawn is the colonist the
// ritual is for (the one being bestowed). Whether the ritual waits and its
// preconditions hold (the throne room stands) is native's check when it
// applies.
type Ritual struct {
	pawn   PawnID
	ritual RitualKind
	verb   RitualVerb
}

func NewRitual(pawn PawnID, ritual RitualKind, verb RitualVerb) (Ritual, error) {
	if !validID(string(pawn)) {
		return Ritual{}, errors.New("ritual requires a valid pawn identity")
	}
	if ritual != RitualBestowing {
		return Ritual{}, errors.New("unsupported ritual kind")
	}
	if verb != RitualStart {
		return Ritual{}, errors.New("unsupported ritual verb")
	}
	return Ritual{pawn: pawn, ritual: ritual, verb: verb}, nil
}

func (r Ritual) Pawn() PawnID       { return r.pawn }
func (r Ritual) Ritual() RitualKind { return r.ritual }
func (r Ritual) Verb() RitualVerb   { return r.verb }

func NewRitualAction(id ActionID, ritual Ritual) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewRitual(ritual.pawn, ritual.ritual, ritual.verb); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: RitualAction, ritual: ritual}, nil
}

func (a Action) Ritual() (Ritual, bool) { return a.ritual, a.kind == RitualAction }
