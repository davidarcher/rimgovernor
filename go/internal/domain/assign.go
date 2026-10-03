package domain

import "errors"

// PreviousAssignment mirrors the wire Assignment oneof (entity_id or Clear):
// the thing of the assigned kind (a bed, a throne, a grave) the pawn
// currently owns, as the caller expects it at admission time, or explicitly
// none. Exactly one of the two is set.
type PreviousAssignment struct {
	id    string
	clear bool
}

// ClearPrevious asserts the pawn currently owns no thing of the kind.
func ClearPrevious() PreviousAssignment { return PreviousAssignment{clear: true} }

// KnownPrevious asserts the pawn currently owns the exact thing identified by id.
func KnownPrevious(id string) (PreviousAssignment, error) {
	if !validID(id) {
		return PreviousAssignment{}, errors.New("invalid previous assignment identity")
	}
	return PreviousAssignment{id: id}, nil
}
func (p PreviousAssignment) valid() bool { return p.clear != (p.id != "") }
func (p PreviousAssignment) Clear() bool { return p.clear }
func (p PreviousAssignment) ID() string  { return p.id }

// Assign is explicit intent to assign one already-observed pawn to one
// already-observed assignable thing (a bed, a throne: anything with
// CompAssignableToPawn), replacing the pawn's previously observed
// assignment of that kind (if any), sent as an AssignIntent on
// Actions/Apply. Native resolves the thing's CompAssignableToPawn, refuses a
// thing that has none, and checks reachability and current suitability when
// it applies.
type Assign struct {
	pawn     PawnID
	thing    string
	previous PreviousAssignment
	swap     bool
}

// AsSwap flags the assignment as a bedroom swap (#1243): native evicts the
// bed's current owner instead of refusing an owned bed.
func (a Assign) AsSwap() Assign { a.swap = true; return a }
func (a Assign) Swap() bool     { return a.swap }

func NewAssign(pawn PawnID, thing string, previous PreviousAssignment) (Assign, error) {
	if !validID(string(pawn)) || !validID(thing) || string(pawn) == thing || !previous.valid() {
		return Assign{}, errors.New("assign requires a valid pawn, thing and previous-assignment expectation")
	}
	if !previous.clear && previous.id == thing {
		return Assign{}, errors.New("assign previous assignment must differ from the assigned thing")
	}
	return Assign{pawn: pawn, thing: thing, previous: previous}, nil
}

func (a Assign) Pawn() PawnID                 { return a.pawn }
func (a Assign) Thing() string                { return a.thing }
func (a Assign) Previous() PreviousAssignment { return a.previous }

func NewAssignAction(id ActionID, assign Assign) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewAssign(assign.pawn, assign.thing, assign.previous); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: AssignAction, assign: assign}, nil
}

func (a Action) Assign() (Assign, bool) {
	return a.assign, a.kind == AssignAction
}
