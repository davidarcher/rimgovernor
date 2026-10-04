package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

const RitualAction ActionKind = "ritual"

// RitualKind names the ritual a Ritual command addresses; RitualVerb what it
// asks of it. A start addresses a ritual kind native resolves against the
// game (bestowing); a begin addresses a held ritual precept by its id, so an
// Ideology ritual adds no kind here and no list of ritual defs.
type RitualKind string
type RitualVerb string

const (
	// RitualBestowing is the Empire's bestowing ceremony of the colonist the
	// Ritual names.
	RitualBestowing RitualKind = "bestowing"
	// RitualStart is the player's command that begins a ritual whose lord
	// waits for it (the bestower's Command_BestowerCeremony gizmo).
	RitualStart RitualVerb = "start"
	// RitualBegin starts a held ritual precept with no lord waiting, the way
	// the game's begin-ritual dialog does (#1659).
	RitualBegin RitualVerb = "begin"
)

// RitualSlot is the pawns that fill one role slot of the ritual's behavior,
// named by the slot id the definition catalog lists (RitualRole.id of the behavior's roles).
type RitualSlot struct {
	Slot  string
	Pawns []PawnID
}

// ritualAssignments is the canonical JSON a begin keeps its comparable
// assignments in: slots sorted by id (each slot's pawns in the given order,
// which the game reads as priority), then the spectators.
type ritualAssignments struct {
	Slots      []RitualSlot
	Spectators []PawnID
}

// Ritual is a player command to one ritual (#1639, epic #1598; #1659, epic
// #1653).
//
// A start (kind bestowing) starts the ritual that is waiting for the command;
// pawn is the colonist the ritual is for (the one being bestowed). Whether
// the ritual waits and its preconditions hold (the throne room stands) is
// native's check when it applies.
//
// A begin (kind the held ritual precept's id) starts that precept with no
// lord waiting: pawn is the organizer, spot the ritual's target cell, slots
// the exact pawns filling the behavior's role slots and spectators the pawns
// that attend without a role. Whether the precept may start now and each
// pawn may fill its slot is native's check when it applies.
type Ritual struct {
	pawn       PawnID
	ritual     RitualKind
	verb       RitualVerb
	spot       Cell
	assignment string // canonical JSON of ritualAssignments; empty for a start
}

func NewRitual(pawn PawnID, ritual RitualKind, verb RitualVerb) (Ritual, error) {
	if !validID(string(pawn)) {
		return Ritual{}, errors.New("ritual requires a valid pawn identity")
	}
	if ritual != RitualBestowing {
		return Ritual{}, errors.New("unsupported ritual kind")
	}
	if verb != RitualStart {
		return Ritual{}, errors.New("unsupported ritual verb")
	}
	return Ritual{pawn: pawn, ritual: ritual, verb: verb}, nil
}

// NewRitualBegin is the begin command of the ritual precept with the given
// id. Every pawn appears once across the slots and spectators, each slot is
// named once and holds a pawn.
func NewRitualBegin(organizer PawnID, precept string, spot Cell, slots []RitualSlot, spectators []PawnID) (Ritual, error) {
	if !validID(string(organizer)) {
		return Ritual{}, errors.New("ritual begin requires a valid organizer identity")
	}
	if !validID(precept) || RitualKind(precept) == RitualBestowing {
		return Ritual{}, errors.New("ritual begin requires a ritual precept id")
	}
	if spot.X < 0 || spot.Z < 0 {
		return Ritual{}, errors.New("ritual begin requires a spot cell on the map")
	}
	seenSlots := map[string]bool{}
	seenPawns := map[PawnID]bool{}
	claim := func(pawn PawnID) error {
		if !validID(string(pawn)) {
			return errors.New("ritual begin names an invalid pawn identity")
		}
		if seenPawns[pawn] {
			return errors.New("ritual begin names a pawn twice")
		}
		seenPawns[pawn] = true
		return nil
	}
	ownedSlots := make([]RitualSlot, len(slots))
	for i, slot := range slots {
		if !validID(slot.Slot) {
			return Ritual{}, errors.New("ritual begin requires a valid role slot id")
		}
		if seenSlots[slot.Slot] {
			return Ritual{}, errors.New("ritual begin names a role slot twice")
		}
		seenSlots[slot.Slot] = true
		if len(slot.Pawns) == 0 {
			return Ritual{}, errors.New("ritual begin names a role slot without pawns")
		}
		for _, pawn := range slot.Pawns {
			if err := claim(pawn); err != nil {
				return Ritual{}, err
			}
		}
		ownedSlots[i] = RitualSlot{Slot: slot.Slot, Pawns: append([]PawnID(nil), slot.Pawns...)}
	}
	for _, pawn := range spectators {
		if err := claim(pawn); err != nil {
			return Ritual{}, err
		}
	}
	sort.Slice(ownedSlots, func(a, b int) bool { return ownedSlots[a].Slot < ownedSlots[b].Slot })
	encoded, err := json.Marshal(ritualAssignments{Slots: ownedSlots, Spectators: append([]PawnID{}, spectators...)})
	if err != nil {
		return Ritual{}, err
	}
	return Ritual{pawn: organizer, ritual: RitualKind(precept), verb: RitualBegin, spot: spot, assignment: string(encoded)}, nil
}

// validRitual reports whether r is a ritual NewRitual or NewRitualBegin
// builds.
func validRitual(r Ritual) error {
	if r.verb == RitualBegin {
		built, err := NewRitualBegin(r.pawn, string(r.ritual), r.spot, r.Slots(), r.Spectators())
		if err == nil && built != r {
			err = errors.New("ritual begin is not canonical")
		}
		return err
	}
	if r.spot != (Cell{}) || r.assignment != "" {
		return errors.New("ritual start carries no spot or assignments")
	}
	_, err := NewRitual(r.pawn, r.ritual, r.verb)
	return err
}

func (r Ritual) Pawn() PawnID       { return r.pawn }
func (r Ritual) Ritual() RitualKind { return r.ritual }
func (r Ritual) Verb() RitualVerb   { return r.verb }

// Spot is a begin's target cell; zero for a start.
func (r Ritual) Spot() Cell { return r.spot }

func (r Ritual) decoded() ritualAssignments {
	var out ritualAssignments
	if r.assignment != "" {
		_ = json.Unmarshal([]byte(r.assignment), &out)
	}
	return out
}

// Slots are a begin's role slot assignments, sorted by slot id.
func (r Ritual) Slots() []RitualSlot { return r.decoded().Slots }

// Spectators are a begin's pawns attending without a role.
func (r Ritual) Spectators() []PawnID { return r.decoded().Spectators }

func NewRitualAction(id ActionID, ritual Ritual) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if err := validRitual(ritual); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: RitualAction, ritual: ritual}, nil
}

func (a Action) Ritual() (Ritual, bool) { return a.ritual, a.kind == RitualAction }
