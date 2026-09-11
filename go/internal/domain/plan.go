package domain

import (
	"errors"
	"fmt"
)

type Cell struct{ X, Z int32 }
type Rotation string

const (
	North Rotation = "north"
	East  Rotation = "east"
	South Rotation = "south"
	West  Rotation = "west"
)

type ActionKind string

const BuildingAction ActionKind = "building"
const OwnedDraftAction ActionKind = "owned_draft"
const MeleeAttackAction ActionKind = "melee_attack"

// Building is one resolved placement. Native discovery owns definition existence,
// footprint, map bounds, costs and placement legality; these are not inferred here.
type Building struct {
	definition string
	cell       Cell
	rotation   Rotation
	stuff      string
}

func NewBuilding(definition string, cell Cell, rotation Rotation, stuff string) (Building, error) {
	if !nativeText(definition, false) || !nativeText(stuff, true) {
		return Building{}, errors.New("invalid native definition or stuff text")
	}
	if cell.X < 0 || cell.Z < 0 {
		return Building{}, errors.New("building anchor must be nonnegative")
	}
	switch rotation {
	case North, East, South, West:
	default:
		return Building{}, errors.New("invalid building rotation")
	}
	return Building{definition, cell, rotation, stuff}, nil
}
func (b Building) Definition() string { return b.definition }
func (b Building) Cell() Cell         { return b.cell }
func (b Building) Rotation() Rotation { return b.rotation }

// Empty Stuff requests the native default material selection.
func (b Building) Stuff() string { return b.stuff }
func nativeText(s string, allowEmpty bool) bool {
	return (s == "" && allowEmpty) || validID(s)
}

// Action is a closed variant. New families require constructor and handler coverage.
type Action struct {
	id       ActionID
	kind     ActionKind
	building Building
	draft    OwnedDraft
	melee    MeleeAttack
}

func NewBuildingAction(id ActionID, building Building) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewBuilding(building.definition, building.cell, building.rotation, building.stuff); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: BuildingAction, building: building}, nil
}
func (a Action) ID() ActionID               { return a.id }
func (a Action) Kind() ActionKind           { return a.kind }
func (a Action) Building() (Building, bool) { return a.building, a.kind == BuildingAction }
func SupportedActionKinds() []ActionKind {
	return []ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction}
}
func ValidateHandlerCoverage(kinds []ActionKind) error {
	seen := make(map[ActionKind]bool)
	for _, kind := range kinds {
		if (kind != BuildingAction && kind != OwnedDraftAction && kind != MeleeAttackAction) || seen[kind] {
			return fmt.Errorf("unknown or duplicate action handler %q", kind)
		}
		seen[kind] = true
	}
	for _, kind := range SupportedActionKinds() {
		if !seen[kind] {
			return fmt.Errorf("missing action handler %q", kind)
		}
	}
	return nil
}

type PlanSpec struct {
	id       PlanID
	revision PlanRevision
	actions  []Action
}

func NewPlan(id PlanID, revision PlanRevision, actions []Action) (PlanSpec, error) {
	if !validID(string(id)) {
		return PlanSpec{}, errors.New("invalid plan identity")
	}
	seen := make(map[ActionID]Action)
	for _, a := range actions {
		var canonical Action
		var err error
		switch a.kind {
		case BuildingAction:
			canonical, err = NewBuildingAction(a.id, a.building)
		case OwnedDraftAction:
			canonical, err = NewOwnedDraftAction(a.id, a.draft)
		case MeleeAttackAction:
			canonical, err = NewMeleeAttackAction(a.id, a.melee)
		default:
			return PlanSpec{}, errors.New("unsupported action variant")
		}
		if err != nil {
			return PlanSpec{}, err
		}
		if canonical != a {
			return PlanSpec{}, errors.New("mixed action variants")
		}
		if _, exists := seen[a.id]; exists {
			return PlanSpec{}, fmt.Errorf("duplicate action identity %q", a.id)
		}
		if a.kind == MeleeAttackAction {
			prerequisite, exists := seen[a.melee.draftAction]
			if !exists || prerequisite.kind != OwnedDraftAction || prerequisite.draft.pawn != a.melee.pawn {
				return PlanSpec{}, errors.New("melee attack requires its preceding owned draft for the same pawn")
			}
		}
		seen[a.id] = a
	}
	return PlanSpec{id, revision, append([]Action(nil), actions...)}, nil
}
func (p PlanSpec) ID() PlanID             { return p.id }
func (p PlanSpec) Revision() PlanRevision { return p.revision }
func (p PlanSpec) Actions() []Action      { return append([]Action(nil), p.actions...) }
