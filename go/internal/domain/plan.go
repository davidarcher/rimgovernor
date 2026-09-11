package domain

import (
	"errors"
	"fmt"
	"strings"
	"unicode/utf8"
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
	if !utf8.ValidString(s) {
		return false
	}
	if s == "" {
		return allowEmpty
	}
	if strings.TrimSpace(s) == "" {
		return false
	}
	n := 0
	for _, r := range s {
		if r > 0xffff {
			n += 2
		} else {
			n++
		}
		if r == 0 {
			return false
		}
	}
	return n <= 200
}

// Action is a closed variant. New families require constructor and handler coverage.
type Action struct {
	id       ActionID
	kind     ActionKind
	building Building
}

func NewBuildingAction(id ActionID, building Building) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewBuilding(building.definition, building.cell, building.rotation, building.stuff); err != nil {
		return Action{}, err
	}
	return Action{id, BuildingAction, building}, nil
}
func (a Action) ID() ActionID               { return a.id }
func (a Action) Kind() ActionKind           { return a.kind }
func (a Action) Building() (Building, bool) { return a.building, a.kind == BuildingAction }
func SupportedActionKinds() []ActionKind    { return []ActionKind{BuildingAction} }
func ValidateHandlerCoverage(kinds []ActionKind) error {
	seen := make(map[ActionKind]bool)
	for _, kind := range kinds {
		if kind != BuildingAction || seen[kind] {
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
	seen := make(map[ActionID]bool)
	for _, a := range actions {
		if a.kind != BuildingAction {
			return PlanSpec{}, errors.New("unsupported action variant")
		}
		if _, err := NewBuildingAction(a.id, a.building); err != nil {
			return PlanSpec{}, err
		}
		if seen[a.id] {
			return PlanSpec{}, fmt.Errorf("duplicate action identity %q", a.id)
		}
		seen[a.id] = true
	}
	return PlanSpec{id, revision, append([]Action(nil), actions...)}, nil
}
func (p PlanSpec) ID() PlanID             { return p.id }
func (p PlanSpec) Revision() PlanRevision { return p.revision }
func (p PlanSpec) Actions() []Action      { return append([]Action(nil), p.actions...) }
