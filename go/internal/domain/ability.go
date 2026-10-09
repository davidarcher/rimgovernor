package domain

import (
	"errors"
	"fmt"
	"strings"
)

// AbilityAction invokes one pawn ability through Actions/Apply. Native guards own
// eligibility, including favor, cooldown, range and hostility. Applied proves use and
// cooldown start; arrivals and combat effects require separate observations.
const AbilityAction ActionKind = "ability"

// AbilitySourceKind names where an ability comes from.
type AbilitySourceKind string

const (
	// AbilityPermit is an acting royal permit (royalAid) the pawn holds with a
	// faction.
	AbilityPermit AbilitySourceKind = "permit"
	// AbilityPsycast is a psycast (AbilityDef) the pawn knows.
	AbilityPsycast AbilitySourceKind = "psycast"
)

// AbilitySource is an immutable, comparable value. Def is the permit's
// RoyalTitlePermitDef defName; Faction is the faction def the permit is held
// with.
type AbilitySource struct {
	kind    AbilitySourceKind
	faction string
	def     string
}

// PermitSource is the permit source for a faction def and a permit def.
func PermitSource(faction, permit string) (AbilitySource, error) {
	if !validID(faction) || !validID(permit) || strings.ContainsRune(faction, ':') || strings.ContainsRune(permit, ':') {
		return AbilitySource{}, errors.New("ability permit source requires a faction def and a permit def")
	}
	return AbilitySource{kind: AbilityPermit, faction: faction, def: permit}, nil
}

// PsycastSource is the psycast source for an AbilityDef defName.
func PsycastSource(ability string) (AbilitySource, error) {
	if !validID(ability) || strings.ContainsRune(ability, ':') {
		return AbilitySource{}, errors.New("ability psycast source requires an ability def")
	}
	return AbilitySource{kind: AbilityPsycast, def: ability}, nil
}

func (s AbilitySource) Kind() AbilitySourceKind { return s.kind }

// Faction is the faction def of a permit source, empty for other sources.
func (s AbilitySource) Faction() string { return s.faction }

// Def is the source's ability def: the permit def of a permit source, the
// AbilityDef of a psycast source.
func (s AbilitySource) Def() string { return s.def }

// Key is the source's stable single-string form, "permit:<faction>:<permit>"
// or "psycast:<abilityDef>".
func (s AbilitySource) Key() string {
	switch s.kind {
	case AbilityPermit:
		return string(s.kind) + ":" + s.faction + ":" + s.def
	case AbilityPsycast:
		return string(s.kind) + ":" + s.def
	}
	return ""
}

// ParseAbilitySource is Key's inverse.
func ParseAbilitySource(key string) (AbilitySource, error) {
	parts := strings.Split(key, ":")
	if len(parts) == 3 && AbilitySourceKind(parts[0]) == AbilityPermit {
		return PermitSource(parts[1], parts[2])
	}
	if len(parts) == 2 && AbilitySourceKind(parts[0]) == AbilityPsycast {
		return PsycastSource(parts[1])
	}
	return AbilitySource{}, fmt.Errorf("invalid ability source %q", key)
}

// accepts reports whether the source can take a target of the kind. A permit
// calls aid, a strike or a drop at a cell, or takes no target. A psycast
// takes whichever arm the ability needs (none for a self-cast); native checks
// that the arm is the one this ability takes.
func (s AbilitySource) accepts(target AbilityTargetKind) bool {
	switch s.kind {
	case AbilityPermit:
		return target == AbilityTargetNone || target == AbilityTargetCell
	case AbilityPsycast:
		return true
	}
	return false
}

// AbilityTargetKind is what an ability aims at.
type AbilityTargetKind string

const (
	AbilityTargetNone  AbilityTargetKind = "none"
	AbilityTargetCell  AbilityTargetKind = "cell"
	AbilityTargetPawn  AbilityTargetKind = "pawn"
	AbilityTargetThing AbilityTargetKind = "thing"
)

// AbilityTarget is an immutable, comparable value: none, a cell, or the load
// id of a pawn or thing.
type AbilityTarget struct {
	kind AbilityTargetKind
	cell Cell
	id   string
}

func NoAbilityTarget() AbilityTarget { return AbilityTarget{kind: AbilityTargetNone} }

func AbilityCellTarget(cell Cell) (AbilityTarget, error) {
	if cell.X < 0 || cell.Z < 0 {
		return AbilityTarget{}, errors.New("invalid ability target cell")
	}
	return AbilityTarget{kind: AbilityTargetCell, cell: cell}, nil
}

func AbilityPawnTarget(pawn PawnID) (AbilityTarget, error) {
	if !validID(string(pawn)) {
		return AbilityTarget{}, errors.New("invalid ability target pawn")
	}
	return AbilityTarget{kind: AbilityTargetPawn, id: string(pawn)}, nil
}

func AbilityThingTarget(thing string) (AbilityTarget, error) {
	if !validID(thing) {
		return AbilityTarget{}, errors.New("invalid ability target thing")
	}
	return AbilityTarget{kind: AbilityTargetThing, id: thing}, nil
}

func (t AbilityTarget) Kind() AbilityTargetKind { return t.kind }

// Cell is the target cell of a cell target.
func (t AbilityTarget) Cell() Cell { return t.cell }

// ID is the load id of a pawn or thing target.
func (t AbilityTarget) ID() string { return t.id }

// Ability is one pawn using one ability source on one target.
type Ability struct {
	pawn   PawnID
	source AbilitySource
	target AbilityTarget
}

func NewAbility(pawn PawnID, source AbilitySource, target AbilityTarget) (Ability, error) {
	if !validID(string(pawn)) {
		return Ability{}, errors.New("ability requires a valid pawn identity")
	}
	if rebuilt, err := ParseAbilitySource(source.Key()); err != nil || rebuilt != source {
		return Ability{}, errors.New("ability requires a valid source")
	}
	valid := false
	switch target.kind {
	case AbilityTargetNone:
		valid = target == AbilityTarget{kind: AbilityTargetNone}
	case AbilityTargetCell:
		_, err := AbilityCellTarget(target.cell)
		valid = err == nil && target.id == ""
	case AbilityTargetPawn:
		_, err := AbilityPawnTarget(PawnID(target.id))
		valid = err == nil && target.cell == Cell{}
	case AbilityTargetThing:
		_, err := AbilityThingTarget(target.id)
		valid = err == nil && target.cell == Cell{}
	}
	if !valid {
		return Ability{}, errors.New("ability requires a valid target")
	}
	if !source.accepts(target.kind) {
		return Ability{}, fmt.Errorf("a %s ability does not take a %s target", source.kind, target.kind)
	}
	return Ability{pawn: pawn, source: source, target: target}, nil
}

func (a Ability) Pawn() PawnID          { return a.pawn }
func (a Ability) Source() AbilitySource { return a.source }
func (a Ability) Target() AbilityTarget { return a.target }

func NewAbilityAction(id ActionID, ability Ability) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewAbility(ability.pawn, ability.source, ability.target); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: AbilityAction, ability: ability}, nil
}

func (a Action) Ability() (Ability, bool) {
	return a.ability, a.kind == AbilityAction
}
