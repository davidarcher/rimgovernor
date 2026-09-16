package domain

import "errors"

// GearReplace is explicit intent to wear one already-observed apparel or
// weapon replacement already carried, produced or otherwise reachable by an
// already-observed pawn. It reuses the native ImproveGear operation.
// Native reachability, forced/locked gear and current outfit eligibility are
// established at inspection, not here.
type GearReplace struct {
	pawn              PawnID
	thing, definition string
}

func NewGearReplace(pawn PawnID, thing, definition string) (GearReplace, error) {
	if !validID(string(pawn)) || !validID(thing) || !validID(definition) {
		return GearReplace{}, errors.New("gear replace requires a valid pawn, thing and definition identity")
	}
	return GearReplace{pawn: pawn, thing: thing, definition: definition}, nil
}

func (g GearReplace) Pawn() PawnID       { return g.pawn }
func (g GearReplace) Thing() string      { return g.thing }
func (g GearReplace) Definition() string { return g.definition }

func NewGearReplaceAction(id ActionID, replace GearReplace) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewGearReplace(replace.pawn, replace.thing, replace.definition); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: GearReplaceAction, gearReplace: replace}, nil
}

func (a Action) GearReplace() (GearReplace, bool) { return a.gearReplace, a.kind == GearReplaceAction }
