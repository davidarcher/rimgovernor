package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	ob "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// Pawn-setting, skill, waste and bill enums on the wire map onto the vanilla names the
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
	wasteKindNames   = map[ob.WasteKind]string{ob.WasteKind_WASTE_KIND_CORPSE: "corpse", ob.WasteKind_WASTE_KIND_SPOILED: "spoiled", ob.WasteKind_WASTE_KIND_UNWANTED: "unwanted"}
	repeatModeNames  = map[o.RepeatMode]string{o.RepeatMode_REPEAT_MODE_FOREVER: "Forever", o.RepeatMode_REPEAT_MODE_COUNT: "RepeatCount", o.RepeatMode_REPEAT_MODE_TARGET: "TargetCount"}
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

// WasteKindName is policy's waste kind ("corpse", "spoiled", "unwanted").
func WasteKindName(k ob.WasteKind) string { return wasteKindNames[k] }

// RepeatModeName is the vanilla BillRepeatModeDef name of a repeat mode.
func RepeatModeName(m o.RepeatMode) string { return repeatModeNames[m] }
