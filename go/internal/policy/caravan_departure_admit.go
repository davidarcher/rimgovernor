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

// CaravanDeparturePolicy is the bounded subset of Python's ExpeditionPolicy
// this admission enforces before dispatch. Richer risk scoring (temperature,
// goodwill, hostile settlements, concurrent caravan limits) is evaluated by
// the read-only preview/advisory path, not here.
type CaravanDeparturePolicy struct {
	MinimumHomeColonists uint32
	MinimumHomeFoodDays  float64
	KeepHomeDoctor       bool
}

// CaravanCrewFacts describes one already-selected undrafted crew pawn.
type CaravanCrewFacts struct {
	Pawn                      domain.PawnID
	SnapshotToken             string
	Dead, Downed, Drafted     domain.Fact[bool]
	MentalState, PlayerForced domain.Fact[bool]
	QueuedJobs                domain.Fact[uint32]
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
	NativeCanTry           domain.Fact[bool]
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
		for _, fact := range []domain.Fact[bool]{member.Dead, member.Downed, member.Drafted, member.MentalState, member.PlayerForced} {
			if _, known := fact.Value(); !known {
				return refuse(UnknownFacts)
			}
		}
		if _, known := member.QueuedJobs.Value(); !known {
			return refuse(UnknownFacts)
		}
		dead, _ := member.Dead.Value()
		downed, _ := member.Downed.Value()
		drafted, _ := member.Drafted.Value()
		mental, _ := member.MentalState.Value()
		forced, _ := member.PlayerForced.Value()
		queued, _ := member.QueuedJobs.Value()
		if dead || downed {
			return refuse(CaravanCrewUnavailable)
		}
		if drafted || mental || forced || queued != 0 {
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
	eligible, known := f.NativeCanTry.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
