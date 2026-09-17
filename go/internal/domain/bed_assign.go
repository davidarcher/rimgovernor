package domain

import "errors"

// PreviousBed mirrors the wire Assignment oneof (entity_id or Clear): the
// pawn's exact currently-owned bed the caller expects at admission time, or
// explicitly none. Exactly one of the two is set.
type PreviousBed struct {
	id    string
	clear bool
}

// ClearPreviousBed asserts the pawn currently owns no bed.
func ClearPreviousBed() PreviousBed { return PreviousBed{clear: true} }

// KnownPreviousBed asserts the pawn currently owns the exact bed identified by id.
func KnownPreviousBed(id string) (PreviousBed, error) {
	if !validID(id) {
		return PreviousBed{}, errors.New("invalid previous bed identity")
	}
	return PreviousBed{id: id}, nil
}
func (p PreviousBed) valid() bool { return p.clear != (p.id != "") }
func (p PreviousBed) Clear() bool { return p.clear }
func (p PreviousBed) ID() string  { return p.id }

// BedAssign is explicit intent to assign one already-observed undrafted pawn
// to one already-observed bed, replacing its previously observed bed
// ownership (if any). It reuses the native AssignBed operation, the same one
// the legacy JSON home/upkeep_bed tool drives. Native reachability and
// current suitability are established at inspection, not here.
type BedAssign struct {
	pawn     PawnID
	bed      string
	previous PreviousBed
}

func NewBedAssign(pawn PawnID, bed string, previous PreviousBed) (BedAssign, error) {
	if !validID(string(pawn)) || !validID(bed) || string(pawn) == bed || !previous.valid() {
		return BedAssign{}, errors.New("bed assign requires a valid pawn, bed and previous-bed expectation")
	}
	if !previous.clear && previous.id == bed {
		return BedAssign{}, errors.New("bed assign previous bed must differ from the assigned bed")
	}
	return BedAssign{pawn: pawn, bed: bed, previous: previous}, nil
}

func (b BedAssign) Pawn() PawnID             { return b.pawn }
func (b BedAssign) Bed() string              { return b.bed }
func (b BedAssign) PreviousBed() PreviousBed { return b.previous }

func NewBedAssignAction(id ActionID, assign BedAssign) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewBedAssign(assign.pawn, assign.bed, assign.previous); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: BedAssignAction, bedAssign: assign}, nil
}

func (a Action) BedAssign() (BedAssign, bool) {
	return a.bedAssign, a.kind == BedAssignAction
}
