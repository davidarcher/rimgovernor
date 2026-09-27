package bridge

import (
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// FactFamily groups the cacheable observation reads that go stale together.
// Each family tolerates a bounded tick advance (TickTolerance): a row read
// at one tick is served under a later scope while the advance is within the
// family's tolerance, so a step can plan against facts a running clock has
// moved past by a little, and never against facts it has outrun.
type FactFamily string

const (
	FactDefinitions FactFamily = "definitions"
	FactWorld       FactFamily = "world"
	FactIdentity    FactFamily = "identity"
	FactColony      FactFamily = "colony"
	FactPawns       FactFamily = "pawns"
	FactEmergency   FactFamily = "emergency"
	FactRooms       FactFamily = "rooms"
	FactResearch    FactFamily = "research"
)

// FactFamilies lists every family, in wire order.
func FactFamilies() []FactFamily {
	return []FactFamily{FactDefinitions, FactWorld, FactIdentity, FactColony, FactPawns, FactEmergency, FactRooms, FactResearch}
}

// FactTickUnbounded is the tolerance of a family whose rows survive any
// tick advance within one (load, generation) scope.
const FactTickUnbounded int64 = -1

// Tick tolerances, in game ticks (60 ticks a second at Normal speed, 2500
// a game hour). Definitions and world facts do not change with the map's
// ticks. The research page moves on the scale of a day; buildings, zones,
// stock and rooms on the scale of an hour; pawns and the emergency census
// (positions, health, hostiles) within minutes. The identity family's
// payload is the tick itself, so an older row is a wrong fact by
// definition: it serves the same tick only.
const (
	FactTickToleranceIdentity  int64 = 0
	FactTickToleranceResearch  int64 = 60000
	FactTickToleranceColony    int64 = 2500
	FactTickToleranceRooms     int64 = 2500
	FactTickTolerancePawns     int64 = 250
	FactTickToleranceEmergency int64 = 250
)

// TickTolerance is the greatest tick advance a row of the family stays
// valid across, or FactTickUnbounded.
func (f FactFamily) TickTolerance() int64 {
	switch f {
	case FactDefinitions, FactWorld:
		return FactTickUnbounded
	case FactIdentity:
		return FactTickToleranceIdentity
	case FactResearch:
		return FactTickToleranceResearch
	case FactColony:
		return FactTickToleranceColony
	case FactRooms:
		return FactTickToleranceRooms
	case FactPawns:
		return FactTickTolerancePawns
	case FactEmergency:
		return FactTickToleranceEmergency
	}
	return 0
}

// Fresh reports whether a row of the family read at rowTick still serves a
// scope at scopeTick: the scope is never behind the row (a tick rewind is a
// new world) and not ahead of it by more than the tolerance.
func (f FactFamily) Fresh(rowTick, scopeTick int64) bool {
	advance := scopeTick - rowTick
	if advance < 0 {
		return false
	}
	tolerance := f.TickTolerance()
	return tolerance == FactTickUnbounded || advance <= tolerance
}

// FactFamilyFromWire maps a clock ObservationInvalidated family to its
// controller name; an unspecified or unknown value reports false.
func FactFamilyFromWire(v k.FactFamily) (FactFamily, bool) {
	switch v {
	case k.FactFamily_FACT_FAMILY_DEFINITIONS:
		return FactDefinitions, true
	case k.FactFamily_FACT_FAMILY_WORLD:
		return FactWorld, true
	case k.FactFamily_FACT_FAMILY_IDENTITY:
		return FactIdentity, true
	case k.FactFamily_FACT_FAMILY_COLONY:
		return FactColony, true
	case k.FactFamily_FACT_FAMILY_PAWNS:
		return FactPawns, true
	case k.FactFamily_FACT_FAMILY_EMERGENCY:
		return FactEmergency, true
	case k.FactFamily_FACT_FAMILY_ROOMS:
		return FactRooms, true
	case k.FactFamily_FACT_FAMILY_RESEARCH:
		return FactResearch, true
	}
	return "", false
}
