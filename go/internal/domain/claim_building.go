package domain

import "errors"

// ClaimBuilding is an immutable, comparable value: a one-shot claim of one
// exact claimable building for the player (Building.ClaimableBy(player)
// then SetFaction(player) on the native side, #459), CAS-gated by an
// already-observed snapshot token the same way BedMedical gates a flag.
// The token covers the building's faction and, for a casket, whether it
// holds anything; no pawn or Job is involved.
type ClaimBuilding struct {
	thing  string
	before string
}

func NewClaimBuilding(thing, before string) (ClaimBuilding, error) {
	if !validID(thing) || !validID(before) {
		return ClaimBuilding{}, errors.New("invalid claim building identity")
	}
	return ClaimBuilding{thing, before}, nil
}
func (b ClaimBuilding) Thing() string       { return b.thing }
func (b ClaimBuilding) BeforeToken() string { return b.before }

func NewClaimBuildingAction(id ActionID, b ClaimBuilding) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewClaimBuilding(b.thing, b.before)
	if err != nil || canonical != b {
		return Action{}, errors.New("invalid claim building")
	}
	return Action{id: id, kind: ClaimBuildingAction, claimBuilding: b}, nil
}
func (a Action) ClaimBuilding() (ClaimBuilding, bool) {
	return a.claimBuilding, a.kind == ClaimBuildingAction
}
