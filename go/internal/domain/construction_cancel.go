package domain

import "errors"

const ConstructionCancelAction ActionKind = "construction_cancel"

// ConstructionCancel removes one already-issued player construction order --
// a blueprint or a frame -- at one exact cell, and never a completed building
// or a replacement placed since.
//
// Unlike ZoneEdit, which carries the target's native entity identity and CAS
// token in the action itself, this family deliberately carries none. A pending
// construction order has no stable native identity across its own lifetime:
// RimWorld replaces the blueprint with a frame as soon as work starts, so the
// thing ID recorded at issuance is routinely already gone by the time a player
// asks to cancel, while the order itself is still there. What is stable is the
// placement -- the cell, the definition being built and the material -- which
// is exactly the triple Python's construction_cancellation.capture_targets
// matches on. The exact native thing ID and its CAS token are therefore
// discovered at inspection, immediately before dispatch, the same way
// ZoneEdit's dispatch uses its freshly inspected token rather than the one the
// proposal carried, and they fill operations.proto's
// CancelConstruction.target EntityPrecondition there.
//
// Definition and Material mirror the proto's expected_def_name and
// expected_stuff: they are preconditions the native side re-checks, not a
// description of what to remove. An empty Material means the placement was
// issued with native default material selection, matching Building.Stuff.
type ConstructionCancel struct {
	definition string
	stuff      string
	cell       Cell
}

func NewConstructionCancel(definition string, cell Cell, stuff string) (ConstructionCancel, error) {
	if !nativeText(definition, false) || !nativeText(stuff, true) {
		return ConstructionCancel{}, errors.New("invalid native definition or stuff text")
	}
	if cell.X < 0 || cell.Z < 0 {
		return ConstructionCancel{}, errors.New("construction cancel anchor must be nonnegative")
	}
	return ConstructionCancel{definition, stuff, cell}, nil
}

// NewConstructionCancelFor derives the cancellation of one already-committed
// placement. It is the only supported way to build one from a plan: a
// cancellation always names a placement this controller itself issued, never a
// cell a caller described independently.
func NewConstructionCancelFor(b Building) (ConstructionCancel, error) {
	return NewConstructionCancel(b.Definition(), b.Cell(), b.Stuff())
}

// ReconstructConstructionCancel rebuilds a canonical value from one of unknown
// provenance, mirroring ReconstructZoneEdit.
func ReconstructConstructionCancel(c ConstructionCancel) (ConstructionCancel, error) {
	return NewConstructionCancel(c.definition, c.cell, c.stuff)
}

func (c ConstructionCancel) Definition() string { return c.definition }
func (c ConstructionCancel) Cell() Cell         { return c.cell }

// Empty Material means the placement was issued with the native default
// material selection, exactly as Building.Stuff reports it.
func (c ConstructionCancel) Material() string { return c.stuff }

// Matches reports whether one already-committed placement is the one this
// cancellation names, the identity test Python's capture_targets applies per
// cell before it will send anything.
func (c ConstructionCancel) Matches(b Building) bool {
	return c.definition == b.Definition() && c.cell == b.Cell() && c.stuff == b.Stuff()
}

func NewConstructionCancelAction(id ActionID, c ConstructionCancel) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := ReconstructConstructionCancel(c)
	if err != nil || canonical != c {
		return Action{}, errors.New("invalid construction cancel value")
	}
	return Action{id: id, kind: ConstructionCancelAction, constructionCancel: c}, nil
}
func (a Action) ConstructionCancel() (ConstructionCancel, bool) {
	return a.constructionCancel, a.kind == ConstructionCancelAction
}
