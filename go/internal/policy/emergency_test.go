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

// A hostile the colony has not discovered is no emergency: the ancient-danger
// mechanoid sealed behind a shrine wall held the clock on unsafe_colony and
// deselected every development goal, the ClearAncientShrine breach that would
// have released it included (#659).
func TestEmergencyUndiscoveredThreatNeitherHoldsNorCounts(t *testing.T) {
	for _, kind := range []ThreatKind{Hostile, HuntingPredator, HostileBuilding} {
		f := completeEmergency()
		row := EmergencyThreat{ID: "guard", Kind: kind, Dead: domain.Known(false), Downed: domain.Known(false), Fogged: domain.Known(true)}
		if kind == HostileBuilding {
			row.SnapshotToken, row.Definition, row.Cells = "token", "Hive", []domain.Cell{{X: 1, Z: 1}}
		}
		f.Threats = []EmergencyThreat{row}
		if !evaluateEmergency(t, f).Clear {
			t.Fatalf("fogged %v held", kind)
		}
		snapshot, err := NewEmergencySnapshot(emergencyScope(), 10, f)
		if err != nil {
			t.Fatal(err)
		}
		if hostiles, _ := EmergencyNeeds(snapshot, emergencyScope(), 10); !reflect.DeepEqual(hostiles, domain.Known(int64(0))) {
			t.Fatalf("fogged %v counted as a hostile: %v", kind, hostiles)
		}
		// Unread health behind fog is nothing to hold for either.
		f.Threats[0].Dead, f.Threats[0].Downed = domain.Unknown[bool](), domain.Unknown[bool]()
		if !evaluateEmergency(t, f).Clear {
			t.Fatalf("fogged %v with unread health held", kind)
		}
		// Unknown or false fog is a discovered threat and still holds.
		for _, fog := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false)} {
			f.Threats[0].Dead, f.Threats[0].Downed, f.Threats[0].Fogged = domain.Known(false), domain.Known(false), fog
			if !hasEmergencyHold(evaluateEmergency(t, f), EmergencyUnsafeThreat, "guard") {
				t.Fatalf("discovered %v cleared under fog %v", kind, fog)
			}
		}
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
		// A hostile building within the band holds like a hostile pawn (#246);
		// one at the animal watch distance, or of unknown distance, is a
		// squad target only (#340).
		{"hive near", EmergencyThreat{ID: "t", Kind: HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), Distance: domain.Known(3.0), SnapshotToken: "cas", Definition: "Hive", Cells: []domain.Cell{{X: 5, Z: 5}}}, true},
		{"hive far", EmergencyThreat{ID: "t", Kind: HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), Distance: far, SnapshotToken: "cas", Definition: "Hive", Cells: []domain.Cell{{X: 5, Z: 5}}}, false},
		{"hive distance unknown", EmergencyThreat{ID: "t", Kind: HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), SnapshotToken: "cas", Definition: "Hive", Cells: []domain.Cell{{X: 5, Z: 5}}}, true},
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

// A bleeding colonist still on their feet and out of bed is nobody's patient
// (#618): WorkGiver_Tend tends a humanlike in bed only and the ground tend
// needs a downed pawn, so the hold would suspend every goal, including the
// one that builds the bed. The hold returns once they lie down or drop, and
// an unknown InBed keeps it.
func TestEmergencyAmbulatoryBleederOutOfBedIsNotCritical(t *testing.T) {
	bleeder := func(downed, needsTend bool, inBed domain.Fact[bool]) EmergencyFacts {
		f := completeEmergency()
		p := healthyPawn("p")
		p.Downed, p.Bleeding, p.NeedsTend, p.InBed = domain.Known(downed), domain.Known(true), domain.Known(needsTend), inBed
		f.Colonists = []EmergencyPawn{p}
		return f
	}
	if d := evaluateEmergency(t, bleeder(false, true, domain.Known(false))); !d.Clear {
		t.Fatal("up and out of bed held", d)
	}
	if d := evaluateEmergency(t, bleeder(false, true, domain.Known(true))); !hasEmergencyHold(d, EmergencyCriticalMedical, "p") {
		t.Fatal("in bed cleared", d)
	}
	if d := evaluateEmergency(t, bleeder(true, true, domain.Known(false))); !hasEmergencyHold(d, EmergencyCriticalMedical, "p") {
		t.Fatal("downed on the ground cleared", d)
	}
	if d := evaluateEmergency(t, bleeder(false, true, domain.Unknown[bool]())); !hasEmergencyHold(d, EmergencyCriticalMedical, "p") {
		t.Fatal("unknown bed cleared", d)
	}
	// Downed with a tend outstanding stays the emergency regardless of bed.
	f := bleeder(true, true, domain.Known(false))
	f.Colonists[0].Bleeding = domain.Known(false)
	if d := evaluateEmergency(t, f); !hasEmergencyHold(d, EmergencyCriticalMedical, "p") {
		t.Fatal("downed untended cleared", d)
	}
}
