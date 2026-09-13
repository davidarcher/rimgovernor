package domain

import "errors"

// Repair is explicit intent to have one pawn repair one damaged, player-owned
// structure. The pawn is not drafted; native eligibility, current job state and
// the structure's remaining damage are established at inspection, not here.
// Cell is carried so dispatch can re-scope a fresh CAS snapshot token for the
// structure, mirroring Haul's Cell field: the general upkeep census
// deliberately strips snapshot tokens from its rows.
type Repair struct {
	pawn      PawnID
	structure string
	cell      Cell
}

func NewRepair(pawn PawnID, structure string, cell Cell) (Repair, error) {
	if !validID(string(pawn)) || !validID(structure) || cell.X < 0 || cell.Z < 0 {
		return Repair{}, errors.New("repair requires a valid pawn, structure and nonnegative cell")
	}
	return Repair{pawn: pawn, structure: structure, cell: cell}, nil
}

func (r Repair) Pawn() PawnID      { return r.pawn }
func (r Repair) Structure() string { return r.structure }
func (r Repair) Cell() Cell        { return r.cell }

func NewRepairAction(id ActionID, repair Repair) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewRepair(repair.pawn, repair.structure, repair.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: RepairAction, repair: repair}, nil
}

func (a Action) Repair() (Repair, bool) { return a.repair, a.kind == RepairAction }
