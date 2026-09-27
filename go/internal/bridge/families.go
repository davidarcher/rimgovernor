package bridge

import (
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// FactFamily names the observation facts native reports changed together
// (ObservationInvalidated).
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
