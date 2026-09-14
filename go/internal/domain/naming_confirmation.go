package domain

import "errors"

const NamingConfirmationAction ActionKind = "naming_confirmation"

// NamingConfirmation is explicit intent to accept the exact observed
// suggestions in the initial, colony-wide, one-shot faction/settlement naming
// dialog. WindowID plus the suggestions stand in for an EntityPrecondition or
// snapshot token: the native ConfirmColonyNames operation refuses on any
// drift from these exact observed values (see NativeColonyNamingOperations.cs
// and presentation-coverage.md). It is only ever dispatched through the
// ordinary autopilot authority, never PlayerPresentation.Apply's own
// explicit-player confirm_colony_names branch.
type NamingConfirmation struct {
	windowID       int32
	factionName    string
	settlementName string
}

func NewNamingConfirmation(windowID int32, factionName, settlementName string) (NamingConfirmation, error) {
	if windowID < 0 || !validID(factionName) || !validID(settlementName) {
		return NamingConfirmation{}, errors.New("naming confirmation requires a valid window and name suggestions")
	}
	return NamingConfirmation{windowID: windowID, factionName: factionName, settlementName: settlementName}, nil
}

func (n NamingConfirmation) WindowID() int32        { return n.windowID }
func (n NamingConfirmation) FactionName() string    { return n.factionName }
func (n NamingConfirmation) SettlementName() string { return n.settlementName }

func NewNamingConfirmationAction(id ActionID, value NamingConfirmation) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewNamingConfirmation(value.windowID, value.factionName, value.settlementName); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: NamingConfirmationAction, namingConfirmation: value}, nil
}

func (a Action) NamingConfirmation() (NamingConfirmation, bool) {
	return a.namingConfirmation, a.kind == NamingConfirmationAction
}
