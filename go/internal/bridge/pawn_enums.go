package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	ob "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// Pawn-setting and skill enums on the wire map onto the vanilla names the
// domain and policy layers use; an unspecified or unknown value maps to "".

var hostilityWire = map[domain.HostilityResponse]o.HostilityResponse{
	domain.HostilityIgnore: o.HostilityResponse_HOSTILITY_RESPONSE_IGNORE,
	domain.HostilityAttack: o.HostilityResponse_HOSTILITY_RESPONSE_ATTACK,
	domain.HostilityFlee:   o.HostilityResponse_HOSTILITY_RESPONSE_FLEE,
}

var (
	medicalCareNames = invert(medicalCareWire)
	hostilityNames   = invert(hostilityWire)
	passionNames     = map[ob.Passion]string{ob.Passion_PASSION_NONE: "None", ob.Passion_PASSION_MINOR: "Minor", ob.Passion_PASSION_MAJOR: "Major"}
)

func invert[K, V comparable](m map[K]V) map[V]K {
	out := make(map[V]K, len(m))
	for k, v := range m {
		out[v] = k
	}
	return out
}

// MedicalCareName is the MedicalCareCategory name of a wire care tier.
func MedicalCareName(care o.MedicalCare) string { return string(medicalCareNames[care]) }

// HostilityName is the HostilityResponseMode of a wire hostility response.
func HostilityName(h o.HostilityResponse) domain.HostilityResponse { return hostilityNames[h] }

// PassionName is the vanilla Passion name ("None", "Minor", "Major").
func PassionName(p ob.Passion) string { return passionNames[p] }
