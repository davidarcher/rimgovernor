package domain

import "errors"

const GatheringAction ActionKind = "gathering"

// Gathering starts one vanilla GatheringDef through GatheringDef.CanExecute and
// GatheringWorker.TryExecute. It names no spot: the game chooses it, and a built
// PartySpot controls that choice. Ideology precept gatherings are rituals.
type Gathering struct {
	def       string
	organizer PawnID
}

func NewGathering(def string, organizer PawnID) (Gathering, error) {
	if !validID(def) || !validID(string(organizer)) {
		return Gathering{}, errors.New("gathering requires a gathering def and an organizer")
	}
	return Gathering{def, organizer}, nil
}
func (g Gathering) Def() string       { return g.def }
func (g Gathering) Organizer() PawnID { return g.organizer }
func NewGatheringAction(id ActionID, g Gathering) (Action, error) {
	canonical, err := NewGathering(g.def, g.organizer)
	if !validID(string(id)) || err != nil || canonical != g {
		return Action{}, errors.New("invalid gathering action")
	}
	return Action{id: id, kind: GatheringAction, gathering: g}, nil
}
func (a Action) Gathering() (Gathering, bool) { return a.gathering, a.kind == GatheringAction }
