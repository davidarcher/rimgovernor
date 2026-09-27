package domain

import "errors"

// ClaimBuilding is an immutable, comparable value: a one-shot claim of one
// exact claimable building for the player (Building.ClaimableBy(player)
// then SetFaction(player) on the native side, #459; a BuildingPatchIntent since #940); no pawn or Job is involved.
type ClaimBuilding struct {
	thing string
}

func NewClaimBuilding(thing string) (ClaimBuilding, error) {
	if !validID(thing) {
		return ClaimBuilding{}, errors.New("invalid claim building identity")
	}
	return ClaimBuilding{thing}, nil
}
func (b ClaimBuilding) Thing() string { return b.thing }

func NewClaimBuildingAction(id ActionID, b ClaimBuilding) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewClaimBuilding(b.thing)
	if err != nil || canonical != b {
		return Action{}, errors.New("invalid claim building")
	}
	return Action{id: id, kind: ClaimBuildingAction, claimBuilding: b}, nil
}
func (a Action) ClaimBuilding() (ClaimBuilding, bool) {
	return a.claimBuilding, a.kind == ClaimBuildingAction
}
