package domain

import "errors"

// Clean is explicit intent to have one pawn clean one specific filth entity.
// The pawn is not drafted; native eligibility, current job state and whether
// the filth still exists are established at inspection, not here. Cell is
// carried so dispatch can re-scope a fresh CAS snapshot token for the filth,
// mirroring Repair's Cell field: filth has no exact-ID lookup RPC, so its
// token must be resolved by scanning the cell's things instead.
type Clean struct {
	pawn  PawnID
	filth string
	cell  Cell
}

func NewClean(pawn PawnID, filth string, cell Cell) (Clean, error) {
	if !validID(string(pawn)) || !validID(filth) || cell.X < 0 || cell.Z < 0 {
		return Clean{}, errors.New("clean requires a valid pawn, filth and nonnegative cell")
	}
	return Clean{pawn: pawn, filth: filth, cell: cell}, nil
}

func (c Clean) Pawn() PawnID  { return c.pawn }
func (c Clean) Filth() string { return c.filth }
func (c Clean) Cell() Cell    { return c.cell }

func NewCleanAction(id ActionID, clean Clean) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewClean(clean.pawn, clean.filth, clean.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: CleanAction, clean: clean}, nil
}

func (a Action) Clean() (Clean, bool) { return a.clean, a.kind == CleanAction }
