package policy

import (
	"math"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CaravanCrewUnavailable mirrors GearReplacePawnUnavailable/EquipPawnUnavailable:
// a crew member is dead, downed or otherwise cannot be given a fresh order now.
const CaravanCrewUnavailable Reason = "caravan_crew_unavailable"

// CaravanHomeStaffingInsufficient: departure would leave the home map under
// the minimum colonist or doctor requirement.
const CaravanHomeStaffingInsufficient Reason = "caravan_home_staffing_insufficient"

// CaravanHomeFoodInsufficient: the home colony's unreserved food runway after
// the pack leaves would fall under the floor (PlanCaravanCargo).
const CaravanHomeFoodInsufficient Reason = "caravan_home_food_insufficient"

// CaravanRouteUnavailable: native reports no path to the destination.
const CaravanRouteUnavailable Reason = "caravan_route_unavailable"

// CaravanDestinationTemperatureOutOfRange: the destination temperature is
// outside the policy window or unreadable.
const CaravanDestinationTemperatureOutOfRange Reason = "caravan_destination_temperature_out_of_range"

// CaravanDestinationHostile: the destination settlement is hostile.
const CaravanDestinationHostile Reason = "caravan_destination_hostile"

// CaravanDestinationGoodwillInsufficient: the destination faction's goodwill
// is under the policy minimum (only evaluated with a faction stake).
const CaravanDestinationGoodwillInsufficient Reason = "caravan_destination_goodwill_insufficient"

// CaravanDeparturePolicy is the bounded subset of the expedition policy this
// admission enforces before dispatch. MinimumHomeFoodDays is the home stock
// floor PlanCaravanCargo keeps (RoutinePolicy.FoodMinDays);
// TravelFoodMarginDays is the food carried past the route estimate.
// Concurrent caravan limits are advisory scope, not enforced here.
type CaravanDeparturePolicy struct {
	MinimumHomeColonists           uint32
	MinimumHomeFoodDays            float64
	TravelFoodMarginDays           float64
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
	// Cargo is the pack PlanCaravanCargo composed (the action's cargo plus
	// the crew's journey food) and CargoRefusal its refusal when it could
	// not; the preview below was taken for exactly this pack.
	Cargo             CaravanCargoPlan
	CargoRefusal      Reason
	RouteReachable    domain.Fact[bool]
	RouteTemperatureC domain.Fact[float64]
	RouteHostile      domain.Fact[bool]
	// RouteFactionID empty means the destination has no settlement/faction
	// stake, so the goodwill check does not apply; absent and unknown are
	// the same thing here, hence a plain string rather than a Fact.
	RouteFactionID string
	RouteGoodwill  domain.Fact[int32]
	// RouteFoodRotDays is native's own dialog-level days-until-rot for the
	// previewed pack, a diagnostic beside the shelf-life packing above; it
	// is never a refusal.
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
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PawnTick < r.MinimumTick || f.PreviewTick < f.PawnTick {
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
	if f.CargoRefusal != "" {
		return refuse(f.CargoRefusal)
	}
	if len(f.Cargo.Food) == 0 {
		return refuse(UnknownFacts)
	}
	packed := make(map[string]int64, len(f.Cargo.Cargo))
	for _, line := range f.Cargo.Cargo {
		packed[line.Definition] += line.Count
	}
	for _, item := range departure.Cargo() {
		if item.Count > math.MaxInt64 || packed[item.Definition] < int64(item.Count) {
			return refuse(UnknownFacts)
		}
	}
	reachable, known := f.RouteReachable.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !reachable {
		return refuse(CaravanRouteUnavailable)
	}
	// Destination temperature, hostility and goodwill are the strict
	// outbound gates; RouteFoodRotDays is diagnostic only.
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
