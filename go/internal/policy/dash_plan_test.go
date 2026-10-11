package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// At -40 C with a -10 C comfort minimum the pawn is 30 below comfort:
// growth (30-10) x 0.0000645 = 0.00129 per 60 ticks, so the serious floor
// (0.35) is 271 steps = about 16,300 ticks away.
func dashRequest(candidates ...DashCandidate) DashRequest {
	return DashRequest{Now: 1000, Temperature: knownF(-40), ComfortMin: knownF(-10), Severity: knownF(0), Speed: knownF(4.6),
		Candidates: candidates, Tuning: PublishedHypothermiaTuning}
}

func tripIDs(p DashPlan) []string {
	var out []string
	for _, t := range p.Trips {
		out = append(out, t.ID)
	}
	return out
}

func TestPlanDashOrdersByValuePerExposure(t *testing.T) {
	// Steel is worth less in total than the chunk but far cheaper to fetch.
	plan := PlanDash(dashRequest(
		DashCandidate{ID: "chunk", Value: 300, PathCells: 400},
		DashCandidate{ID: "steel-near", Value: 200, PathCells: 40},
		DashCandidate{ID: "meals", Value: 150, PathCells: 80},
	))
	if want := []string{"steel-near", "meals", "chunk"}; !reflect.DeepEqual(tripIDs(plan), want) {
		t.Fatalf("%v %+v", tripIDs(plan), plan)
	}
	if plan.Reason != DashPlanned || plan.Until <= 1000 {
		t.Fatal(plan)
	}
}

func TestPlanDashStaysWithinTheEstimatorBudget(t *testing.T) {
	// A 1500-cell round trip alone is ~39,000 ticks, more than the 16,300
	// the cap allows: it is never planned. The two near trips are.
	plan := PlanDash(dashRequest(
		DashCandidate{ID: "far", Value: 10000, PathCells: 1500},
		DashCandidate{ID: "a", Value: 100, PathCells: 200},
		DashCandidate{ID: "b", Value: 90, PathCells: 250},
	))
	if want := []string{"a", "b"}; !reflect.DeepEqual(tripIDs(plan), want) {
		t.Fatal(tripIDs(plan), plan)
	}
	// The whole set, return included, ends below the cap.
	sev := 0.0
	for _, trip := range plan.Trips {
		sev += float64(trip.Exposure) / hypothermiaInterval * 0.00129
	}
	if sev >= DashSeverityCap {
		t.Fatalf("planned severity %v reaches the cap", sev)
	}
	budget, _ := OutdoorBudgetFor(HazardExtremeCold, knownF(-40), knownF(-10), knownF(0), DashSeverityCap, PublishedHypothermiaTuning).Value()
	total := 0
	for _, trip := range plan.Trips {
		total += trip.Exposure
	}
	if total > budget.Ticks {
		t.Fatalf("planned %d ticks over the estimator budget %d", total, budget.Ticks)
	}
}

func TestPlanDashCountsTheReturnLeg(t *testing.T) {
	// One-way the trip fits twice over; out and back does not.
	oneWay := 1000.0
	if plan := PlanDash(dashRequest(DashCandidate{ID: "x", Value: 1, PathCells: oneWay})); len(plan.Trips) != 0 {
		t.Fatalf("a trip whose return overruns the cap was planned: %+v", plan)
	}
	budget, _ := OutdoorBudgetFor(HazardExtremeCold, knownF(-40), knownF(-10), knownF(0), DashSeverityCap, PublishedHypothermiaTuning).Value()
	if one := int(oneWay / 4.6 * 60); one >= budget.Ticks || 2*one < budget.Ticks {
		t.Fatalf("fixture: one way %d, budget %d", one, budget.Ticks)
	}
}

func TestPlanDashReturnIsSlowerWhileChilled(t *testing.T) {
	trip := DashCandidate{ID: "x", Value: 1, PathCells: 300}
	fresh := PlanDash(dashRequest(trip))
	chilled := dashRequest(trip)
	chilled.Severity = knownF(0.15)
	late := PlanDash(chilled)
	if len(fresh.Trips) != 1 || len(late.Trips) != 1 {
		t.Fatal(fresh, late)
	}
	// Starting at 0.15 the pawn crosses the minor stage (0.20) on the way:
	// the same trip takes longer than the move speed alone says.
	naive := fresh.Trips[0].Exposure
	if late.Trips[0].Exposure <= naive {
		t.Fatalf("a chilled pawn's trip took %d, a fresh pawn's %d", late.Trips[0].Exposure, naive)
	}
}

func TestPlanDashOpensNothingOnUnknowns(t *testing.T) {
	trip := DashCandidate{ID: "x", Value: 1, PathCells: 10}
	for name, edit := range map[string]func(*DashRequest){
		"temperature": func(r *DashRequest) { r.Temperature = domain.Unknown[float64]() },
		"comfort":     func(r *DashRequest) { r.ComfortMin = domain.Unknown[float64]() },
		"severity":    func(r *DashRequest) { r.Severity = domain.Unknown[float64]() },
		"speed":       func(r *DashRequest) { r.Speed = domain.Unknown[float64]() },
		"no speed":    func(r *DashRequest) { r.Speed = knownF(0) },
		"at the cap":  func(r *DashRequest) { r.Severity = knownF(DashSeverityCap) },
	} {
		r := dashRequest(trip)
		edit(&r)
		if p := PlanDash(r); len(p.Trips) != 0 || p.Until != 0 {
			t.Errorf("%s: %+v", name, p)
		}
	}
	if p := PlanDash(dashRequest()); p.Reason != DashNoValue {
		t.Fatal(p)
	}
}

func TestPlanDashHarmlessColdIsOneReviewHorizon(t *testing.T) {
	r := dashRequest(DashCandidate{ID: "x", Value: 1, PathCells: 5000})
	r.ComfortMin = knownF(-45)
	p := PlanDash(r)
	if len(p.Trips) != 1 || p.Until != r.Now+DashUnboundedTicks || p.Reason != DashUnbounded {
		t.Fatal(p)
	}
}

func TestDashWindowClosesAtItsBound(t *testing.T) {
	w := DashWindow{Pawn: "a", Until: 500}
	if !w.OpenAt(499) || w.OpenAt(500) || w.OpenAt(900) {
		t.Fatal("the window must be open before the bound and closed from it")
	}
	if (DashWindow{Until: 500}).OpenAt(1) {
		t.Fatal("a window names a pawn")
	}
}

// dashCold is the shelter fixture under a cold snap at -40 C with an open
// dash window for "a" ending at tick 500.
func dashCold(area string, tick domain.Tick) RoundsFacts {
	f := shelterFacts(area)
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionColdSnap}})
	f.OutdoorTemperature = domain.Known(-40.0)
	f.Tick, f.Dash = tick, domain.Known(DashWindow{Pawn: "a", Until: 500})
	return f
}

func TestShelteringLetsTheDashPawnOutUntilTheBound(t *testing.T) {
	// "a" inside Safe is released, "b" and the pet stay.
	got := PlanSheltering(dashCold("safe", 100))
	want := []AllowedAreaChange{{Pawn: "a", Area: ""}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("%v", got)
	}
	// Outside while open: nothing moves "a"; the others are sheltered.
	got = PlanSheltering(dashCold("", 100))
	want = []AllowedAreaChange{{Pawn: "b", Area: "safe"}, {Pawn: "pet", Animal: true, Area: "safe"}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("%v", got)
	}
}

func TestShelteringRecallsTheColonistStillOutsideAtTheBound(t *testing.T) {
	// Tick 500 is the bound: "a" is outside (unrestricted) and goes back.
	got := PlanSheltering(dashCold("", 500))
	if !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatalf("%v", got)
	}
	// And later still.
	if got = PlanSheltering(dashCold("", 4000)); !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatalf("%v", got)
	}
}

func TestShelteringDashNeverLiftsFalloutOrThreat(t *testing.T) {
	f := dashCold("", 100)
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionToxicFallout}})
	if got := PlanSheltering(f); !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatalf("fallout: %v", got)
	}
	f = dashCold("", 100)
	f.Hostiles = domain.Known(int64(2))
	f.ShelterCombatants = domain.Known([]PawnID{})
	if got := PlanSheltering(f); !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatalf("threat: %v", got)
	}
}

func TestDashLiftsOnlyTheColdHold(t *testing.T) {
	cold := domain.Known([]OutdoorHazard{{HazardExtremeCold}})
	heat := domain.Known([]OutdoorHazard{{HazardExtremeHeat}})
	both := domain.Known([]OutdoorHazard{{HazardExtremeCold}, {HazardToxicFallout}})
	if got := remoteThreatHold(RemoteSafety{Threat: domain.Known(false), Hazard: cold, DashOpen: true}); got != "" {
		t.Fatalf("cold with a window: %q", got)
	}
	if got := remoteThreatHold(RemoteSafety{Threat: domain.Known(false), Hazard: cold}); got != RemoteHoldHazard {
		t.Fatalf("cold without a window: %q", got)
	}
	for _, h := range []domain.Fact[[]OutdoorHazard]{heat, both} {
		if got := remoteThreatHold(RemoteSafety{Threat: domain.Known(false), Hazard: h, DashOpen: true}); got != RemoteHoldHazard {
			t.Fatalf("%v: %q", h, got)
		}
		if got := recoveryHold(RecoveryThing{Evidenced: true}, RecoveryRequest{Hazard: h, DashOpen: true}); got != RemoteHoldHazard {
			t.Fatalf("recovery %v: %q", h, got)
		}
	}
	if got := recoveryHold(RecoveryThing{Evidenced: true, RouteSafe: domain.Known(true)}, RecoveryRequest{Hazard: cold, DashOpen: true}); got != "" {
		t.Fatalf("recovery cold with a window: %q", got)
	}
}

func dashBody(comfort, severity, speed float64) domain.Fact[PawnBody] {
	return domain.Known(PawnBody{ComfortableMinC: knownF(comfort), Hypothermia: knownF(severity), MoveSpeed: knownF(speed)})
}

func TestDashWindowOfNamesThePawnWithTheMostValue(t *testing.T) {
	f := dashCold("", 100)
	f.Dash = domain.Unknown[DashWindow]()
	f.Hostiles = domain.Known(int64(0))
	f.Items = ItemFacts{Market: map[Resource]float64{"Steel": 1.9}}
	f.EventLoot = domain.Known([]LootItem{{
		Supply: StartingSupply{Thing: "s1", Definition: "Steel", Cell: domain.Cell{X: 5, Z: 5}}, Forbidden: true, SafeToHaul: true, SafetyKnown: true,
		Count: 75, PathLength: knownF(120),
	}})
	up := domain.Known(true)
	pawns := domain.Known([]WorkPawn{
		{ID: "a", Available: up, Body: dashBody(-10, 0, 4.6)},
		{ID: "b", Available: up, Body: dashBody(-10, 0, 4.6)},
		{ID: "c", Available: domain.Known(false), Body: dashBody(-10, 0, 4.6)},
		{ID: "d", Available: up},
	})
	w, known := DashWindowOf(f, pawns, 100, PublishedHypothermiaTuning).Value()
	if !known || w.Pawn != "a" || w.Until <= 100 || len(w.Trips) != 1 || w.Trips[0].ID != "s1" {
		t.Fatalf("%v %+v", known, w)
	}
	// A hostile, or a second hazard, closes it.
	f.Hostiles = domain.Known(int64(1))
	if w, _ := DashWindowOf(f, pawns, 100, PublishedHypothermiaTuning).Value(); w.Pawn != "" {
		t.Fatal("window open under threat", w)
	}
	f.Hostiles = domain.Known(int64(0))
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionToxicFallout}})
	if w, _ := DashWindowOf(f, pawns, 100, PublishedHypothermiaTuning).Value(); w.Pawn != "" {
		t.Fatal("window open under fallout", w)
	}
	// Unknown hostiles are unknown, not closed-and-known.
	f.Hostiles = domain.Unknown[int64]()
	if _, known := DashWindowOf(f, pawns, 100, PublishedHypothermiaTuning).Value(); known {
		t.Fatal("unknown hostiles must leave the window unknown")
	}
}
