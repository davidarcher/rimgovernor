package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	ob "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	rp "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
)

// Pawn-setting, skill, waste, bill, combat, route and quest enums on the wire map onto the vanilla names the
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
	wasteKindNames   = map[ob.WasteKind]string{ob.WasteKind_WASTE_KIND_CORPSE: "corpse", ob.WasteKind_WASTE_KIND_SPOILED: "spoiled"}
	repeatModeNames  = map[o.RepeatMode]string{o.RepeatMode_REPEAT_MODE_FOREVER: "Forever", o.RepeatMode_REPEAT_MODE_COUNT: "RepeatCount", o.RepeatMode_REPEAT_MODE_TARGET: "TargetCount"}
	fireModeNames    = map[o.CombatFireMode]string{o.CombatFireMode_COMBAT_FIRE_MODE_AT_WILL: "fire_at_will", o.CombatFireMode_COMBAT_FIRE_MODE_HOLD: "hold_fire"}
	routeKindNames   = map[ob.RouteFacilityKind]string{ob.RouteFacilityKind_ROUTE_FACILITY_KIND_BED: "bed", ob.RouteFacilityKind_ROUTE_FACILITY_KIND_BENCH: "bench", ob.RouteFacilityKind_ROUTE_FACILITY_KIND_STORAGE: "storage", ob.RouteFacilityKind_ROUTE_FACILITY_KIND_DINING: "dining", ob.RouteFacilityKind_ROUTE_FACILITY_KIND_DEFENSE: "defense", ob.RouteFacilityKind_ROUTE_FACILITY_KIND_STOCKPILE: "stockpile"}
	questStatusNames = map[rp.QuestStatus]string{rp.QuestStatus_QUEST_STATUS_NOT_YET_ACCEPTED: "NotYetAccepted", rp.QuestStatus_QUEST_STATUS_ONGOING: "Ongoing", rp.QuestStatus_QUEST_STATUS_ENDED_UNKNOWN_OUTCOME: "EndedUnknownOutcome", rp.QuestStatus_QUEST_STATUS_ENDED_SUCCESS: "EndedSuccess", rp.QuestStatus_QUEST_STATUS_ENDED_FAILED: "EndedFailed", rp.QuestStatus_QUEST_STATUS_ENDED_OFFER_EXPIRED: "EndedOfferExpired", rp.QuestStatus_QUEST_STATUS_ENDED_INVALID: "EndedInvalid"}
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

// WasteKindName is policy's waste kind ("corpse", "spoiled").
func WasteKindName(k ob.WasteKind) string { return wasteKindNames[k] }

// RepeatModeName is the vanilla BillRepeatModeDef name of a repeat mode.
func RepeatModeName(m o.RepeatMode) string { return repeatModeNames[m] }

// FireModeName is policy's fire mode ("fire_at_will", "hold_fire").
func FireModeName(m o.CombatFireMode) string { return fireModeNames[m] }

// RouteKindName is policy's route facility kind ("bed", "bench", ...).
func RouteKindName(k ob.RouteFacilityKind) string { return routeKindNames[k] }

// QuestStatusName is the vanilla QuestState name ("NotYetAccepted", ...).
func QuestStatusName(s rp.QuestStatus) string { return questStatusNames[s] }
