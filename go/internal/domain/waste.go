package domain

import "errors"

// Waste is explicit intent to have one pawn haul or bury one specific exposed
// native waste item (spoiled, a rotting corpse). The pawn is not drafted;
// native checks eligibility, the item and the haul-or-bury destination live
// when the HaulWaste job applies. Cell is the census position the planner saw.
type Waste struct {
	pawn   PawnID
	target string
	cell   Cell
}

func NewWaste(pawn PawnID, target string, cell Cell) (Waste, error) {
	if !validID(string(pawn)) || !validID(target) || cell.X < 0 || cell.Z < 0 {
		return Waste{}, errors.New("waste requires a valid pawn, target and nonnegative cell")
	}
	return Waste{pawn: pawn, target: target, cell: cell}, nil
}

func (w Waste) Pawn() PawnID   { return w.pawn }
func (w Waste) Target() string { return w.target }
func (w Waste) Cell() Cell     { return w.cell }

func NewWasteAction(id ActionID, waste Waste) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewWaste(waste.pawn, waste.target, waste.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: WasteAction, waste: waste}, nil
}

func (a Action) Waste() (Waste, bool) { return a.waste, a.kind == WasteAction }

// CorpseOf is a corpse's inner pawn class, as the waste census reports it
// (#832) and a corpse bill's ingredient filter names it (#833).
type CorpseOf string

const (
	// CorpseColonist is a player-faction humanlike.
	CorpseColonist CorpseOf = "colonist"
	// CorpseStranger is any other humanlike: raiders, visitors, prisoners.
	CorpseStranger CorpseOf = "stranger"
	// CorpseAnimal is any non-humanlike.
	CorpseAnimal CorpseOf = "animal"
)

// Valid reports one of the three classes.
func (c CorpseOf) Valid() bool {
	return c == CorpseColonist || c == CorpseStranger || c == CorpseAnimal
}
