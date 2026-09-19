package domain

import "errors"

// OpenCasket is explicit intent to have one pawn open one filled ancient
// cryptosleep casket (#460). Opening one casket ejects every casket of its
// shrine group at once, so a plan carries a single OpenCasket after the
// drafts and moves that stand a melee colonist in front of each filled
// casket. The pawn may be drafted: the vanilla Open job is an ordered job
// the draft does not refuse. Native eligibility (the casket still holds
// something, the pawn can reach its interaction cell) is established at
// inspection; Cell is the casket's cell, carried like Repair's so a fresh
// CAS token can be re-scoped from the building listing.
type OpenCasket struct {
	pawn   PawnID
	casket string
	cell   Cell
}

func NewOpenCasket(pawn PawnID, casket string, cell Cell) (OpenCasket, error) {
	if !validID(string(pawn)) || !validID(casket) || cell.X < 0 || cell.Z < 0 {
		return OpenCasket{}, errors.New("open casket requires a valid pawn, casket and nonnegative cell")
	}
	return OpenCasket{pawn: pawn, casket: casket, cell: cell}, nil
}

func (o OpenCasket) Pawn() PawnID   { return o.pawn }
func (o OpenCasket) Casket() string { return o.casket }
func (o OpenCasket) Cell() Cell     { return o.cell }

const OpenCasketAction ActionKind = "open_casket"

func NewOpenCasketAction(id ActionID, open OpenCasket) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewOpenCasket(open.pawn, open.casket, open.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: OpenCasketAction, openCasket: open}, nil
}

func (a Action) OpenCasket() (OpenCasket, bool) { return a.openCasket, a.kind == OpenCasketAction }
