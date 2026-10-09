package domain

import "errors"

// IgniteAction is explicit intent to have one pawn throw a molotov at one cell:
// the pawn is drafted, carries the molotov and force-fires
// it at the cell. Native refuses while any pawn stands in the target cell's
// room; Go does not restate that. Applied means the throw order was taken;
// whether the fire caught is a separate observed state.
const IgniteAction ActionKind = "ignite"

// Ignite is one pawn throwing a molotov at one cell.
type Ignite struct {
	pawn PawnID
	cell Cell
}

func NewIgnite(pawn PawnID, cell Cell) (Ignite, error) {
	if !validID(string(pawn)) || cell.X < 0 || cell.Z < 0 {
		return Ignite{}, errors.New("ignite requires a valid pawn and cell")
	}
	return Ignite{pawn: pawn, cell: cell}, nil
}

func (i Ignite) Pawn() PawnID { return i.pawn }
func (i Ignite) Cell() Cell   { return i.cell }

func NewIgniteAction(id ActionID, ignite Ignite) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewIgnite(ignite.pawn, ignite.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: IgniteAction, ignite: ignite}, nil
}

func (a Action) Ignite() (Ignite, bool) { return a.ignite, a.kind == IgniteAction }
