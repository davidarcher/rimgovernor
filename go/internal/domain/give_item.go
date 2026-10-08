package domain

import (
	"errors"
	"math"
)

const GiveItemAction ActionKind = "give_item"

// GiveItem asks vanilla GiveToPawn to fulfill the remaining request. The count
// is an observed precondition, not a cap on the native job's delivery.
type GiveItem struct {
	hauler, recipient PawnID
	definition        string
	expectedRemaining int64
}

func NewGiveItem(hauler, recipient PawnID, definition string, expectedRemaining int64) (GiveItem, error) {
	if !validID(string(hauler)) || !validID(string(recipient)) || hauler == recipient || !validID(definition) || expectedRemaining <= 0 || expectedRemaining > math.MaxInt32 {
		return GiveItem{}, errors.New("give item requires exact actors, definition and positive native remaining count")
	}
	return GiveItem{hauler, recipient, definition, expectedRemaining}, nil
}
func (g GiveItem) Hauler() PawnID           { return g.hauler }
func (g GiveItem) Recipient() PawnID        { return g.recipient }
func (g GiveItem) Definition() string       { return g.definition }
func (g GiveItem) ExpectedRemaining() int64 { return g.expectedRemaining }
func NewGiveItemAction(id ActionID, g GiveItem) (Action, error) {
	canonical, err := NewGiveItem(g.hauler, g.recipient, g.definition, g.expectedRemaining)
	if !validID(string(id)) || err != nil || canonical != g {
		return Action{}, errors.New("invalid give item action")
	}
	return Action{id: id, kind: GiveItemAction, giveItem: g}, nil
}
func (a Action) GiveItem() (GiveItem, bool) { return a.giveItem, a.kind == GiveItemAction }
