package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// FactFamily is the policy registry's family name for the observation facts
// native reports changed together (ObservationInvalidated).
type FactFamily = policy.FactFamily

const (
	FactDefinitions = policy.FactDefinitions
	FactWorld       = policy.FactWorld
	FactIdentity    = policy.FactIdentity
	FactColony      = policy.FactColony
	FactPawns       = policy.FactPawns
	FactEmergency   = policy.FactEmergency
	FactRooms       = policy.FactRooms
	FactResearch    = policy.FactResearch
)

// FactFamilies lists every family, in wire order.
func FactFamilies() []FactFamily { return policy.FactFamilies() }

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
