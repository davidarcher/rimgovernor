package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"reflect"
	"strings"
	"testing"
)

func emergencyScope() domain.GenerationSnapshot {
	return domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1, Native: 3}
}
func healthyPawn(id PawnID) EmergencyPawn {
	return EmergencyPawn{ID: id, Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}
}
func completeEmergency() EmergencyFacts {
	return EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}
}
func evaluateEmergency(t *testing.T, facts EmergencyFacts) EmergencyDecision {
	t.Helper()
	s, e := NewEmergencySnapshot(emergencyScope(), 10, facts)
	if e != nil {
		t.Fatal(e)
	}
	return EvaluateEmergency(s, emergencyScope(), 10)
}
func hasEmergencyHold(d EmergencyDecision, r EmergencyReason, id PawnID) bool {
	for _, h := range d.Holds {
		if h.Reason == r && h.Pawn == id {
			return true
		}
	}
	return false
}
func TestEmergencyKnownAndMedicalFacts(t *testing.T) {
	if !evaluateEmergency(t, completeEmergency()).Clear {
		t.Fatal("known empty held")
	}
	for _, complete := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false)} {
		f := completeEmergency()
		f.ThreatsComplete = complete
		if !hasEmergencyHold(evaluateEmergency(t, f), EmergencyUnknownFacts, "") {
			t.Fatal("partial cleared")
		}
		f = completeEmergency()
		f.ColonistsComplete = complete
		if evaluateEmergency(t, f).Clear {
			t.Fatal("partial people cleared")
		}
	}
	for _, field := range []string{"dead", "downed", "bleeding", "tend"} {
		for _, known := range []bool{false, true} {
			p := healthyPawn("p")
			v := domain.Unknown[bool]()
			if known {
				v = domain.Known(true)
			}
			switch field {
			case "dead":
				p.Dead = v
			case "downed":
				p.Downed = v
			case "bleeding":
				p.Bleeding = v
			case "tend":
				p.NeedsTend = v
			}
			f := completeEmergency()
			f.Colonists = []EmergencyPawn{p}
			d := evaluateEmergency(t, f)
			if field == "dead" && known {
				if !d.Clear {
					t.Fatal(d)
				}
				continue
			}
			if (field == "tend" || field == "downed") && known {
				// Needing tending alone is the tend planner's patient
				// (#66), and downed with nothing to tend is the rescue
				// planner's (#304): neither holds every dispatch.
				if !d.Clear {
					t.Fatal(field, d)
				}
				continue
			}
			reason := EmergencyUnknownFacts
			if known {
				reason = EmergencyCriticalMedical
			}
			if !hasEmergencyHold(d, reason, "p") {
				t.Fatal(field, known, d)
			}
		}
	}
	f := completeEmergency()
	f.Colonists = []EmergencyPawn{{ID: "dead", Dead: domain.Known(true)}}
	if !evaluateEmergency(t, f).Clear {
		t.Fatal("dead pawn required health")
	}
	// Downed with a tend outstanding is medical work the colony must do now.
	p := healthyPawn("p")
	p.Downed, p.NeedsTend = domain.Known(true), domain.Known(true)
	f = completeEmergency()
	f.Colonists = []EmergencyPawn{p}
	if !hasEmergencyHold(evaluateEmergency(t, f), EmergencyCriticalMedical, "p") {
		t.Fatal("downed untended pawn cleared")
	}
}
func TestEmergencyThreatCategoriesAndContradictions(t *testing.T) {
	for _, kind := range []ThreatKind{Hostile, HuntingPredator, IgnoredHunter, NearbyPredator, NearbyDowned} {
		f := completeEmergency()
		f.Threats = []EmergencyThreat{{ID: "t", Kind: kind, Dead: domain.Known(false), Downed: domain.Known(false)}}
		d := evaluateEmergency(t, f)
		if d.Clear != (kind != Hostile && kind != HuntingPredator) {
			t.Fatal(kind, d)
		}
		f.Threats[0].Dead = domain.Unknown[bool]()
		if evaluateEmergency(t, f).Clear {
			t.Fatal("unknown cleared", kind)
		}
		f.Threats[0].Downed = domain.Known(true)
		if !evaluateEmergency(t, f).Clear {
			t.Fatal("incapacitated held", kind)
		}
		f.Threats[0].Dead = domain.Known(true)
		f.Threats[0].Downed = domain.Unknown[bool]()
		if !evaluateEmergency(t, f).Clear {
			t.Fatal("dead held", kind)
		}
	}
	f := completeEmergency()
	f.Threats = []EmergencyThreat{{ID: "same", Kind: Hostile, Dead: domain.Known(true)}, {ID: "same", Kind: NearbyPredator, Dead: domain.Known(true)}}
	if !evaluateEmergency(t, f).Clear {
		t.Fatal("matching crosscategory held")
	}
	f.Threats[1].Dead = domain.Known(false)
	f.Threats[1].Downed = domain.Known(true)
	if !hasEmergencyHold(evaluateEmergency(t, f), EmergencyUnknownFacts, "same") {
		t.Fatal("contradiction cleared")
	}
	f = completeEmergency()
	f.Colonists = []EmergencyPawn{healthyPawn("same")}
	f.Threats = []EmergencyThreat{{ID: "same", Kind: NearbyDowned, Dead: domain.Known(false), Downed: domain.Known(true)}}
	if evaluateEmergency(t, f).Clear {
		t.Fatal("cross census contradiction cleared")
	}
}
func TestEmergencyFreshnessAndValidation(t *testing.T) {
	scope := emergencyScope()
	s, e := NewEmergencySnapshot(scope, 10, completeEmergency())
	if e != nil {
		t.Fatal(e)
	}
	for _, mutate := range []func(*domain.GenerationSnapshot){func(v *domain.GenerationSnapshot) { v.Colony = "other" }, func(v *domain.GenerationSnapshot) { v.Map++ }, func(v *domain.GenerationSnapshot) { v.Load = "other" }, func(v *domain.GenerationSnapshot) { v.Plan = "other" }, func(v *domain.GenerationSnapshot) { v.Revision++ }, func(v *domain.GenerationSnapshot) { v.Native++ }, func(v *domain.GenerationSnapshot) { v.Native++ }} {
		changed := scope
		mutate(&changed)
		if !hasEmergencyHold(EvaluateEmergency(s, changed, 10), EmergencyStaleFacts, "") {
			t.Fatal(changed)
		}
	}
	if EvaluateEmergency(s, scope, 11).Clear || EvaluateEmergency(EmergencySnapshot{}, scope, 0).Clear || EvaluateEmergency(s, scope, -1).Clear {
		t.Fatal("invalid freshness cleared")
	}
	if !EvaluateEmergency(s, scope, 0).Clear {
		t.Fatal("earlier minimum rejected")
	}
	for _, id := range []PawnID{"", " ", "bad\x00id", PawnID(string([]byte{255})), PawnID(strings.Repeat("x", 257))} {
		f := completeEmergency()
		f.Colonists = []EmergencyPawn{{ID: id}}
		if _, e := NewEmergencySnapshot(scope, 0, f); e == nil {
			t.Fatal(id)
		}
	}
	for _, f := range []EmergencyFacts{{Colonists: []EmergencyPawn{{ID: "p"}, {ID: "p"}}}, {Threats: []EmergencyThreat{{ID: "p", Kind: Hostile}, {ID: "p", Kind: Hostile}}}, {Threats: []EmergencyThreat{{ID: "p"}}}, {Colonists: make([]EmergencyPawn, 257)}, {Threats: make([]EmergencyThreat, 257)}} {
		if _, e := NewEmergencySnapshot(scope, 0, f); e == nil {
			t.Fatal("invalid census accepted")
		}
	}
	if _, e := NewEmergencySnapshot(scope, -1, completeEmergency()); e == nil {
		t.Fatal("negative tick")
	}
	if _, e := NewEmergencySnapshot(domain.GenerationSnapshot{}, 0, completeEmergency()); e == nil {
		t.Fatal("invalid scope")
	}
}
func TestEmergencyImmutableAndDeterministic(t *testing.T) {
	f := completeEmergency()
	f.Colonists = []EmergencyPawn{{ID: "b"}, {ID: "a"}}
	s, e := NewEmergencySnapshot(emergencyScope(), 10, f)
	if e != nil {
		t.Fatal(e)
	}
	first := EvaluateEmergency(s, emergencyScope(), 10)
	f.Colonists[0] = healthyPawn("changed")
	f.ThreatsComplete = domain.Known(false)
	if !reflect.DeepEqual(first, EvaluateEmergency(s, emergencyScope(), 10)) {
		t.Fatal("input alias")
	}
	first.Holds[0].Pawn = "changed"
	if reflect.DeepEqual(first, EvaluateEmergency(s, emergencyScope(), 10)) {
		t.Fatal("output alias")
	}
	reversed := completeEmergency()
	reversed.Colonists = []EmergencyPawn{{ID: "a"}, {ID: "b"}}
	if !reflect.DeepEqual(evaluateEmergency(t, reversed), EvaluateEmergency(s, emergencyScope(), 10)) {
		t.Fatal("input order changed decision")
	}
}

func TestEmergencyDistantAnimalThreatIsWatchedNotHeld(t *testing.T) {
	live := func(kind ThreatKind, animal domain.Fact[bool], distance domain.Fact[float64]) EmergencyThreat {
		return EmergencyThreat{ID: "t", Kind: kind, Dead: domain.Known(false), Downed: domain.Known(false), Animal: animal, Distance: distance}
	}
	far, near := domain.Known(DistantThreatCells), domain.Known(DistantThreatCells-1)
	cases := []struct {
		name   string
		threat EmergencyThreat
		held   bool
	}{
		{"manhunter far", live(Hostile, domain.Known(true), far), false},
		{"predator hunting far", live(HuntingPredator, domain.Known(true), far), false},
		{"manhunter near", live(Hostile, domain.Known(true), near), true},
		{"predator hunting near", live(HuntingPredator, domain.Known(true), near), true},
		{"raider far", live(Hostile, domain.Known(false), far), true},
		{"race unknown far", live(Hostile, domain.Unknown[bool](), far), true},
		{"distance unknown animal", live(Hostile, domain.Known(true), domain.Unknown[float64]()), true},
		{"legacy row", live(Hostile, domain.Unknown[bool](), domain.Unknown[float64]()), true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			f := completeEmergency()
			f.Colonists = []EmergencyPawn{healthyPawn("p")}
			f.Threats = []EmergencyThreat{c.threat}
			d := evaluateEmergency(t, f)
			if hasEmergencyHold(d, EmergencyUnsafeThreat, "t") != c.held || d.Clear == c.held {
				t.Fatal(d)
			}
			if c.threat.DistantThreat() == c.held {
				t.Fatal("DistantThreat disagrees with the hold")
			}
		})
	}
	// A dead or downed distant animal is no threat either way, and a distant
	// animal with unknown status still holds as unknown facts.
	f := completeEmergency()
	f.Threats = []EmergencyThreat{{ID: "t", Kind: Hostile, Dead: domain.Unknown[bool](), Downed: domain.Known(false), Animal: domain.Known(true), Distance: far}}
	if d := evaluateEmergency(t, f); !hasEmergencyHold(d, EmergencyUnknownFacts, "t") || hasEmergencyHold(d, EmergencyUnsafeThreat, "t") {
		t.Fatal(d)
	}
	for _, bad := range []float64{-1, math.NaN(), math.Inf(1)} {
		f := completeEmergency()
		f.Threats = []EmergencyThreat{live(Hostile, domain.Known(true), domain.Known(bad))}
		if _, err := NewEmergencySnapshot(emergencyScope(), 10, f); err == nil {
			t.Fatal("invalid distance accepted", bad)
		}
	}
}
