package domain

import "errors"

const CoverClearanceAction ActionKind = "cover_clearance"

// Cover clearance designations are the native designation defs the game's
// own designators place: Mine on a mineable, CutPlant on a plant, Haul on a
// chunk, Deconstruct on a building.
const (
	CoverClearanceMine        = "Mine"
	CoverClearanceCutPlant    = "CutPlant"
	CoverClearanceHaul        = "Haul"
	CoverClearanceDeconstruct = "Deconstruct"
)

// CoverClearance designates one observed cover thing for removal (#581): the
// exact thing by identity at its cell, with the one designation its kind
// needs. The designation is the whole write; ordinary work removes the thing.
type CoverClearance struct {
	thing, definition, designation string
	cell                           Cell
}

func ValidCoverClearanceDesignation(designation string) bool {
	switch designation {
	case CoverClearanceMine, CoverClearanceCutPlant, CoverClearanceHaul, CoverClearanceDeconstruct:
		return true
	}
	return false
}

func NewCoverClearance(thing, definition, designation string, cell Cell) (CoverClearance, error) {
	if !validID(thing) || !validID(definition) || !ValidCoverClearanceDesignation(designation) || cell.X < 0 || cell.Z < 0 {
		return CoverClearance{}, errors.New("invalid cover clearance identity, designation or cell")
	}
	return CoverClearance{thing, definition, designation, cell}, nil
}
func (c CoverClearance) Thing() string       { return c.thing }
func (c CoverClearance) Definition() string  { return c.definition }
func (c CoverClearance) Designation() string { return c.designation }
func (c CoverClearance) Cell() Cell          { return c.cell }
func NewCoverClearanceAction(id ActionID, clearance CoverClearance) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewCoverClearance(clearance.thing, clearance.definition, clearance.designation, clearance.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: CoverClearanceAction, coverClearance: clearance}, nil
}
func (a Action) CoverClearance() (CoverClearance, bool) {
	return a.coverClearance, a.kind == CoverClearanceAction
}
