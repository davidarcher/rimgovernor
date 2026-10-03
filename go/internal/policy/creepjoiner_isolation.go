package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Isolating a creepjoiner (#1740, epic #1694): until its downside shows the
// colony keeps it apart, in the isolation room (isolation_room.go), by
// restricting its allowed area to the Isolation area. It is released when the
// downside shows (a visible trait or hediff a downside def adds, or the
// tracker fired) or when the colony's record says its inspection is done, and
// stays out of the room while it is hungry: a restricted pawn treats food
// outside its area as forbidden.

// IsolationMove is one creepjoiner's allowed-area change: Area is the
// Isolation area's native load id to hold it in, or "" to release it.
type IsolationMove struct {
	Pawn PawnID
	Area string
}

// holdsApart reports whether the colony holds the creepjoiner apart: its
// downside is known not to have shown and the record holds no finished
// inspection of it. Unknown while its downside cannot be read.
func (d CreepJoinerDownsides) holdsApart(h CreepJoinerHand, record CreepJoinerRecord) domain.Fact[bool] {
	if record.Inspections[h.Pawn] == InspectionDone {
		return domain.Known(false)
	}
	revealed, known := d.Revealed(h.Facts).Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(!revealed)
}

// Isolated are the creepjoiners the colony holds apart, sorted by pawn id.
// Unknown while a creepjoiner's downside cannot be read, or one's creepjoiner
// status cannot.
func (d CreepJoinerDownsides) Isolated(hands []CreepJoinerHand, record CreepJoinerRecord) domain.Fact[[]PawnID] {
	var out []PawnID
	for _, h := range hands {
		joiner, known := h.Facts.CreepJoiner.Value()
		if !known {
			return domain.Unknown[[]PawnID]()
		}
		if joiner == nil {
			continue
		}
		apart, known := d.holdsApart(h, record).Value()
		if !known {
			return domain.Unknown[[]PawnID]()
		}
		if apart {
			out = append(out, h.Pawn)
		}
	}
	slices.Sort(out)
	return domain.Known(out)
}

// IsolationMoves are the allowed-area changes the colony owes its
// creepjoiners, sorted by pawn id. area is the Isolation area's load id ("" if
// the map has none); ready is whether the isolation room stands with its bed.
// A creepjoiner held apart, available and not hungry is moved into the area
// once ready; one in the area that is no longer held apart, or is hungry, is
// released. A pawn whose area, availability or hunger is unread is left as
// it is.
func (d CreepJoinerDownsides) IsolationMoves(hands []CreepJoinerHand, record CreepJoinerRecord, area string, ready bool) []IsolationMove {
	if area == "" {
		return nil
	}
	var out []IsolationMove
	for _, h := range hands {
		if joiner, known := h.Facts.CreepJoiner.Value(); !known || joiner == nil {
			continue
		}
		current, ck := h.Area.Value()
		if !ck {
			continue
		}
		apart, ak := d.holdsApart(h, record).Value()
		hungry, hk := h.Hungry.Value()
		available, vk := h.Available.Value()
		switch {
		case current == area && ak && (!apart || hk && hungry):
			out = append(out, IsolationMove{Pawn: h.Pawn, Area: ""})
		case current != area && ready && ak && apart && hk && !hungry && vk && available:
			out = append(out, IsolationMove{Pawn: h.Pawn, Area: area})
		}
	}
	slices.SortFunc(out, func(a, b IsolationMove) int {
		switch {
		case a.Pawn < b.Pawn:
			return -1
		case a.Pawn > b.Pawn:
			return 1
		}
		return 0
	})
	return out
}
