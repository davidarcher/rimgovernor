package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func caravanDepartureRequest(t *testing.T) CaravanDepartureRequest {
	t.Helper()
	departure, _ := domain.NewCaravanDeparture([]domain.PawnID{"alpha", "beta"}, []domain.CargoItem{{Definition: "MealSimple", Count: 10}}, 42)
	a, _ := domain.NewCaravanDepartureAction("caravan-departure", departure)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	member := func(pawn domain.PawnID) CaravanCrewFacts {
		return CaravanCrewFacts{Pawn: pawn, SnapshotToken: "cas-" + string(pawn), Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0))}
	}
	facts := CaravanDepartureFacts{
		Snapshot: s, PawnTick: 12, PreviewTick: 13,
		Crew:                   []CaravanCrewFacts{member("alpha"), member("beta")},
		CatalogToken:           "catalog-cas",
		RemainingHomeColonists: domain.Known(uint32(3)),
		HomeDoctorAvailable:    domain.Known(true),
		HomeFoodRunwayDays:     domain.Known(20.0),
		RouteReachable:         domain.Known(true),
		RouteTemperatureC:      domain.Known(15.0),
		RouteHostile:           domain.Known(false),
		RouteFactionID:         "faction-1",
		RouteGoodwill:          domain.Known(int32(0)),
		RouteFoodRotDays:       domain.Known(9.0),
		NativeCanTry:           domain.Known(true),
	}
	policy := CaravanDeparturePolicy{MinimumHomeColonists: 1, MinimumHomeFoodDays: 5, KeepHomeDoctor: true, MinimumDestinationTemperatureC: -10, MaximumDestinationTemperatureC: 40, MinimumGoodwill: -50}
	return CaravanDepartureRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Policy: policy, Facts: facts}
}

func TestCaravanDepartureAdmission(t *testing.T) {
	r := caravanDepartureRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateCaravanDeparture(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

// TestCaravanDepartureAdmissionNoFactionStake mirrors evaluate_expedition's
// `if route.get('factionId') and ...` gate: an empty-wilds destination with
// no settlement/faction carries no goodwill requirement at all, even when
// goodwill itself is unknown.
func TestCaravanDepartureAdmissionNoFactionStake(t *testing.T) {
	r := caravanDepartureRequest(t)
	r.Facts.RouteFactionID, r.Facts.RouteGoodwill = "", domain.Unknown[int32]()
	if d := EvaluateCaravanDeparture(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
}

func TestCaravanDepartureDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*CaravanDepartureRequest)
		reason Reason
	}{
		{"zero action", func(r *CaravanDepartureRequest) { r.Action = domain.Action{} }, NotReady},
		{"zero progress", func(r *CaravanDepartureRequest) { r.Progress = domain.Progress{} }, NotReady},
		{"cancelled", func(r *CaravanDepartureRequest) { r.Progress, _ = r.Progress.Cancel() }, NotReady},
		{"minimum", func(r *CaravanDepartureRequest) { r.MinimumTick = 13 }, StaleFacts},
		{"negative minimum", func(r *CaravanDepartureRequest) { r.MinimumTick = -1 }, StaleFacts},
		{"reversed interval", func(r *CaravanDepartureRequest) { r.Facts.PreviewTick = 11 }, StaleFacts},
		{"zero generation", func(r *CaravanDepartureRequest) { r.Current.Native = 0 }, StaleFacts},
		{"native", func(r *CaravanDepartureRequest) { r.Current.Native++ }, StaleFacts},
		{"direction", func(r *CaravanDepartureRequest) { r.Current.Direction++ }, StaleFacts},
		{"colony", func(r *CaravanDepartureRequest) { r.Current.Colony = "other" }, StaleFacts},
		{"load", func(r *CaravanDepartureRequest) { r.Current.Load = "other" }, StaleFacts},
		{"map", func(r *CaravanDepartureRequest) { r.Current.Map++ }, StaleFacts},
		{"plan", func(r *CaravanDepartureRequest) { r.Current.Plan = "other" }, StaleFacts},
		{"revision", func(r *CaravanDepartureRequest) { r.Current.Revision++ }, StaleFacts},
		{"catalog CAS", func(r *CaravanDepartureRequest) { r.Facts.CatalogToken = "" }, UnknownFacts},
		{"missing crew member", func(r *CaravanDepartureRequest) { r.Facts.Crew = r.Facts.Crew[:1] }, UnknownFacts},
		{"crew CAS", func(r *CaravanDepartureRequest) { r.Facts.Crew[0].SnapshotToken = "" }, UnknownFacts},
		{"wrong crew pawn", func(r *CaravanDepartureRequest) { r.Facts.Crew[0].Pawn = "other" }, UnknownFacts},
		{"crew dead", func(r *CaravanDepartureRequest) { r.Facts.Crew[0].Dead = domain.Known(true) }, CaravanCrewUnavailable},
		{"crew downed", func(r *CaravanDepartureRequest) { r.Facts.Crew[0].Downed = domain.Known(true) }, CaravanCrewUnavailable},
		{"crew drafted", func(r *CaravanDepartureRequest) { r.Facts.Crew[0].Drafted = domain.Known(true) }, PlayerOrder},
		{"crew mental state", func(r *CaravanDepartureRequest) { r.Facts.Crew[0].MentalState = domain.Known(true) }, PlayerOrder},
		{"crew player forced", func(r *CaravanDepartureRequest) { r.Facts.Crew[0].PlayerForced = domain.Known(true) }, PlayerOrder},
		{"crew queued", func(r *CaravanDepartureRequest) { r.Facts.Crew[0].QueuedJobs = domain.Known(uint32(1)) }, PlayerOrder},
		{"crew unknown queue", func(r *CaravanDepartureRequest) { r.Facts.Crew[0].QueuedJobs = domain.Unknown[uint32]() }, UnknownFacts},
		{"unknown home colonists", func(r *CaravanDepartureRequest) { r.Facts.RemainingHomeColonists = domain.Unknown[uint32]() }, UnknownFacts},
		{"insufficient home colonists", func(r *CaravanDepartureRequest) { r.Facts.RemainingHomeColonists = domain.Known(uint32(0)) }, CaravanHomeStaffingInsufficient},
		{"unknown home doctor", func(r *CaravanDepartureRequest) { r.Facts.HomeDoctorAvailable = domain.Unknown[bool]() }, UnknownFacts},
		{"no home doctor", func(r *CaravanDepartureRequest) { r.Facts.HomeDoctorAvailable = domain.Known(false) }, CaravanHomeStaffingInsufficient},
		{"unknown home food", func(r *CaravanDepartureRequest) { r.Facts.HomeFoodRunwayDays = domain.Unknown[float64]() }, UnknownFacts},
		{"insufficient home food", func(r *CaravanDepartureRequest) { r.Facts.HomeFoodRunwayDays = domain.Known(1.0) }, CaravanHomeFoodInsufficient},
		{"unknown route", func(r *CaravanDepartureRequest) { r.Facts.RouteReachable = domain.Unknown[bool]() }, UnknownFacts},
		{"unreachable route", func(r *CaravanDepartureRequest) { r.Facts.RouteReachable = domain.Known(false) }, CaravanRouteUnavailable},
		{"unknown temperature", func(r *CaravanDepartureRequest) { r.Facts.RouteTemperatureC = domain.Unknown[float64]() }, UnknownFacts},
		{"temperature too cold", func(r *CaravanDepartureRequest) { r.Facts.RouteTemperatureC = domain.Known(-11.0) }, CaravanDestinationTemperatureOutOfRange},
		{"temperature too hot", func(r *CaravanDepartureRequest) { r.Facts.RouteTemperatureC = domain.Known(41.0) }, CaravanDestinationTemperatureOutOfRange},
		{"unknown hostile", func(r *CaravanDepartureRequest) { r.Facts.RouteHostile = domain.Unknown[bool]() }, UnknownFacts},
		{"hostile destination", func(r *CaravanDepartureRequest) { r.Facts.RouteHostile = domain.Known(true) }, CaravanDestinationHostile},
		{"unknown goodwill", func(r *CaravanDepartureRequest) { r.Facts.RouteGoodwill = domain.Unknown[int32]() }, UnknownFacts},
		{"insufficient goodwill", func(r *CaravanDepartureRequest) { r.Facts.RouteGoodwill = domain.Known(int32(-51)) }, CaravanDestinationGoodwillInsufficient},
		{"preview refusal", func(r *CaravanDepartureRequest) { r.Facts.NativeCanTry = domain.Known(false) }, NativeIneligible},
		{"unknown preview", func(r *CaravanDepartureRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }, UnknownFacts},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := caravanDepartureRequest(t)
			c.change(&r)
			d := EvaluateCaravanDeparture(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != c.reason {
				t.Fatal(d)
			}
		})
	}
}
