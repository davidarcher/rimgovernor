package domain

import "errors"

// AutoHomeAreaAction sets the game's home-area auto-expand
// (Find.PlaySettings.autoHomeArea, a save-level setting): an
// AutoHomeAreaIntent on Actions/Apply. No pawn, map cell or Job is involved.
const AutoHomeAreaAction ActionKind = "auto_home_area"

// NewAutoHomeAreaAction builds the action that leaves auto-expand enabled
// or disabled.
func NewAutoHomeAreaAction(id ActionID, enabled bool) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	return Action{id: id, kind: AutoHomeAreaAction, autoHomeArea: enabled}, nil
}

// AutoHomeArea is the auto-expand value the action sets.
func (a Action) AutoHomeArea() (bool, bool) {
	return a.autoHomeArea, a.kind == AutoHomeAreaAction
}
