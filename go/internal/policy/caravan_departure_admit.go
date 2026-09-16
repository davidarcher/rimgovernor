package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CaravanCrewUnavailable mirrors GearReplacePawnUnavailable/EquipPawnUnavailable:
// a crew member is dead, downed or otherwise cannot be given a fresh order now.
const CaravanCrewUnavailable Reason = "caravan_crew_unavailable"

// CaravanHomeStaffingInsufficient mirrors Python's expedition_policy home
// staffing/doctor checks: departure would leave the home map under the
// player's minimum colonist or doctor requirement.
const CaravanHomeStaffingInsufficient Reason = "caravan_home_staffing_insufficient"

// CaravanHomeFoodInsufficient mirrors expedition_policy.home_food_after: the
// remaining home colony's food runway after crew/cargo depart would fall
// below the player's minimum.
const CaravanHomeFoodInsufficient Reason = "caravan_home_food_insufficient"

// CaravanRouteUnavailable mirrors expedition_policy.evaluate_expedition's
// route/reachability gate for a 'form' action.
const CaravanRouteUnavailable Reason = "caravan_route_unavailable"

// CaravanDestinationTemperatureOutOfRange mirrors expedition_policy's
// temperature gate: evaluate_expedition treats a temperature outside
// [minimum_destination_temperature, maximum_destination_temperature], or an
// unreadable temperature, as blocking for every action except 'return'. This
// vertical only ever represents a 'form' action, so the 'return' relaxation
// never applies here.
const CaravanDestinationTemperatureOutOfRange Reason = "caravan_destination_temperature_out_of_range"

// CaravanDestinationHostile mirrors evaluate_expedition's unconditional
// hostile-settlement gate: hostile blocks regardless of action.
const CaravanDestinationHostile Reason = "caravan_destination_hostile"

// CaravanDestinationGoodwillInsufficient mirrors evaluate_expedition's
// goodwill gate: only evaluated when the destination has a faction stake
// (route.get('factionId')), and only blocking for actions other than
// 'return' -- again always true for this 'form'-only vertical.
const CaravanDestinationGoodwillInsufficient Reason = "caravan_destination_goodwill_insufficient"

// CaravanDeparturePolicy is the bounded subset of Python's ExpeditionPolicy
// this admission enforces before dispatch. Concurrent caravan limits remain
// read-only preview/advisory scope, not here; destination temperature,
// hostility and goodwill are enforced below, mirroring
// expedition_policy.evaluate_expedition's non-'return' branch (this vertical
// only ever represents a 'form' action).
type CaravanDeparturePolicy struct {
	MinimumHomeColonists           uint32
	MinimumHomeFoodDays            float64
	KeepHomeDoctor                 bool
	MinimumDestinationTemperatureC float64
	MaximumDestinationTemperatureC float64
	MinimumGoodwill                int32
}

// CaravanCrewFacts describes one already-selected undrafted crew pawn.
type CaravanCrewFacts struct {
	Pawn                  domain.PawnID
	SnapshotToken         string
	Dead, Downed, Drafted domain.Fact[bool]
	MentalState           domain.Fact[bool]
}

type CaravanDepartureFacts struct {
	Snapshot               domain.GenerationSnapshot
	PawnTick, PreviewTick  domain.Tick
	Crew                   []CaravanCrewFacts
	CatalogToken           string
	RemainingHomeColonists domain.Fact[uint32]
	HomeDoctorAvailable    domain.Fact[bool]
	HomeFoodRunwayDays     domain.Fact[float64]
	RouteReachable         domain.Fact[bool]
	RouteTemperatureC      domain.Fact[float64]
	RouteHostile           domain.Fact[bool]
	// RouteFactionID mirrors Python's route.get('factionId') truthiness gate:
	// empty means the destination has no settlement/faction stake, so the
	// goodwill check below does not apply, exactly like CatalogToken/
	// SnapshotToken elsewhere in this file it is a plain validated string,
	// not a Fact, because "absent" (no faction) and "unknown" are the same
	// thing here -- Python never distinguishes them either.
	RouteFactionID string
	RouteGoodwill  domain.Fact[int32]
	// RouteFoodRotDays mirrors expedition_policy.evaluate_expedition's
	// route.get('foodRotDays'): a warning-level signal only (rot arriving
	// before the required travel-food margin), never blocking in Python
	// regardless of action. This admission gate only ever admits or refuses,
	// so the fact is carried for completeness/future advisory use but never
	// checked for refusal here.
	RouteFoodRotDays domain.Fact[float64]
	NativeCanTry     domain.Fact[bool]
}

type CaravanDepartureRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Policy      CaravanDeparturePolicy
	Facts       CaravanDepartureFacts
}

// EvaluateCaravanDeparture re-validates one already-selected crew/cargo/
// destination triple immediately before dispatch, the same shape
// EvaluateGearReplace/EvaluateEquip use. Admission proves eligibility now; it
// does not prove the native FormCaravan job will be issued, accepted, or that
// the caravan actually departs (that is observed later by the executor).
func EvaluateCaravanDeparture(r CaravanDepartureRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	departure, ok := r.Action.CaravanDeparture()
	canonical, err := domain.NewCaravanDepartureAction(r.Action.ID(), departure)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || r.Current.Direction == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PawnTick < r.MinimumTick || f.PreviewTick < f.PawnTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PawnTick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if !validToken(f.CatalogToken) {
		return refuse(UnknownFacts)
	}
	crew := departure.Crew()
	if len(f.Crew) != len(crew) {
		return refuse(UnknownFacts)
	}
	byPawn := make(map[domain.PawnID]CaravanCrewFacts, len(f.Crew))
	for _, member := range f.Crew {
		if !validToken(member.SnapshotToken) {
			return refuse(UnknownFacts)
		}
		byPawn[member.Pawn] = member
	}
	for _, pawn := range crew {
		member, present := byPawn[pawn]
		if !present || member.Pawn != pawn {
			return refuse(UnknownFacts)
		}
		for _, fact := range []domain.Fact[bool]{member.Dead, member.Downed, member.Drafted, member.MentalState} {
			if _, known := fact.Value(); !known {
				return refuse(UnknownFacts)
			}
		}
		dead, _ := member.Dead.Value()
		downed, _ := member.Downed.Value()
		drafted, _ := member.Drafted.Value()
		mental, _ := member.MentalState.Value()
		if dead || downed {
			return refuse(CaravanCrewUnavailable)
		}
		if drafted || mental {
			return refuse(PlayerOrder)
		}
	}
	remaining, known := f.RemainingHomeColonists.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if remaining < r.Policy.MinimumHomeColonists {
		return refuse(CaravanHomeStaffingInsufficient)
	}
	if r.Policy.KeepHomeDoctor {
		doctor, known := f.HomeDoctorAvailable.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if !doctor {
			return refuse(CaravanHomeStaffingInsufficient)
		}
	}
	runway, known := f.HomeFoodRunwayDays.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if runway < r.Policy.MinimumHomeFoodDays {
		return refuse(CaravanHomeFoodInsufficient)
	}
	reachable, known := f.RouteReachable.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !reachable {
		return refuse(CaravanRouteUnavailable)
	}
	// Destination temperature, hostility and goodwill mirror
	// expedition_policy.evaluate_expedition's non-'return' branch: this
	// vertical only ever represents a 'form' action, so the checks below are
	// always the strict (blocking) variant Python applies when action !=
	// 'return'. foodRotDays is a warning-only signal in Python for every
	// action and is never gated here (see CaravanDepartureFacts.RouteFoodRotDays).
	temperature, known := f.RouteTemperatureC.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if temperature < r.Policy.MinimumDestinationTemperatureC || temperature > r.Policy.MaximumDestinationTemperatureC {
		return refuse(CaravanDestinationTemperatureOutOfRange)
	}
	hostile, known := f.RouteHostile.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if hostile {
		return refuse(CaravanDestinationHostile)
	}
	if f.RouteFactionID != "" {
		goodwill, known := f.RouteGoodwill.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if goodwill < r.Policy.MinimumGoodwill {
			return refuse(CaravanDestinationGoodwillInsufficient)
		}
	}
	eligible, known := f.NativeCanTry.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
