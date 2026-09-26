package policy

import (
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func defensiveThreat(id string) DefensiveThreatFacts {
	return DefensiveThreatFacts{ID: PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false), Humanlike: domain.Known(true),
		LordJobClass: domain.Known("LordJob_AssaultColony"), LordToilClass: domain.Known("LordToil_AssaultColony"), NearestColonistDistance: domain.Known(40.0), Position: domain.Known(domain.Cell{X: 9, Z: 5})}
}
func defensiveDefender(id string, ranged bool) SquadDefenderFacts {
	return SquadDefenderFacts{ID: domain.PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false),
		PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ViolenceCapable: domain.Known(true), NeedsTend: domain.Known(false), HealthFraction: domain.Known(1.0),
		RangedEquipped: domain.Known(ranged), MeleeEquipped: domain.Known(!ranged), Armed: domain.Known(true)}
}

func TestSelectDefensivePositionsHoldsTheLine(t *testing.T) {
	firing := cells(9, 23, 8, 23, 10, 23)
	threats := []DefensiveThreatFacts{defensiveThreat("raider-2"), defensiveThreat("raider-1")}
	defenders := []SquadDefenderFacts{defensiveDefender("colonist-b", true), defensiveDefender("colonist-a", true), defensiveDefender("colonist-c", false)}
	got, ok := SelectDefensivePositions(firing, domain.North, threats, defenders)
	want := []DefensivePosition{{Defender: "colonist-a", Cell: domain.Cell{X: 9, Z: 23}, Target: "raider-1"}, {Defender: "colonist-b", Cell: domain.Cell{X: 8, Z: 23}, Target: "raider-1"}}
	if !ok || !reflect.DeepEqual(got, want) {
		t.Fatalf("%v %+v", ok, got)
	}
	threats[1].Dead = domain.Known(true)
	if got, ok = SelectDefensivePositions(firing, domain.North, threats, defenders); !ok || got[0].Target != "raider-2" {
		t.Fatal(got, ok)
	}
}
func TestSelectDefensivePositionsFallsBack(t *testing.T) {
	firing := cells(9, 23)
	defenders := []SquadDefenderFacts{defensiveDefender("colonist-a", true)}
	for name, threat := range map[string]func(*DefensiveThreatFacts){
		"sapper":           func(f *DefensiveThreatFacts) { f.LordToilClass = domain.Known("LordToil_AssaultColonySappers") },
		"breach":           func(f *DefensiveThreatFacts) { f.LordToilClass = domain.Known("LordToil_AssaultColonyBreaching") },
		"siege":            func(f *DefensiveThreatFacts) { f.LordJobClass = domain.Known("LordJob_Siege") },
		"no lord":          func(f *DefensiveThreatFacts) { f.LordJobClass = domain.Unknown[string]() },
		"unknown toil":     func(f *DefensiveThreatFacts) { f.LordToilClass = domain.Unknown[string]() },
		"already engaged":  func(f *DefensiveThreatFacts) { f.NearestColonistDistance = domain.Known(6.0) },
		"unknown distance": func(f *DefensiveThreatFacts) { f.NearestColonistDistance = domain.Unknown[float64]() },
		"on the cover row": func(f *DefensiveThreatFacts) { f.Position = domain.Known(domain.Cell{X: 12, Z: 22}) },
		"behind the line":  func(f *DefensiveThreatFacts) { f.Position = domain.Known(domain.Cell{X: 9, Z: 30}) },
		"unknown position": func(f *DefensiveThreatFacts) { f.Position = domain.Unknown[domain.Cell]() },
		"animal":           func(f *DefensiveThreatFacts) { f.Humanlike = domain.Known(false) },
		"unknown dead":     func(f *DefensiveThreatFacts) { f.Dead = domain.Unknown[bool]() },
	} {
		f := defensiveThreat("raider-1")
		threat(&f)
		if got, ok := SelectDefensivePositions(firing, domain.North, []DefensiveThreatFacts{defensiveThreat("raider-2"), f}, defenders); ok {
			t.Fatal(name, "positioned", got)
		}
	}
	if _, ok := SelectDefensivePositions(nil, domain.North, []DefensiveThreatFacts{defensiveThreat("raider-1")}, defenders); ok {
		t.Fatal("positioned without a layout")
	}
	if _, ok := SelectDefensivePositions(firing, domain.North, []DefensiveThreatFacts{defensiveThreat("raider-1")}, []SquadDefenderFacts{defensiveDefender("colonist-a", false)}); ok {
		t.Fatal("melee defender positioned")
	}
	hurt := defensiveDefender("colonist-a", true)
	hurt.HealthFraction = domain.Known(0.4)
	if _, ok := SelectDefensivePositions(firing, domain.North, []DefensiveThreatFacts{defensiveThreat("raider-1")}, []SquadDefenderFacts{hurt}); ok {
		t.Fatal("wounded defender positioned")
	}
	all := []DefensiveThreatFacts{defensiveThreat("raider-1")}
	all[0].Downed = domain.Known(true)
	if _, ok := SelectDefensivePositions(firing, domain.North, all, defenders); ok {
		t.Fatal("positioned against no live hostile")
	}
}

func TestSelectDefensivePositionsSeatsShootersFirst(t *testing.T) {
	front := defensiveDefender("a", true)
	front.FrontLine = true
	positions, ok := SelectDefensivePositions([]domain.Cell{{X: 1, Z: 1}}, domain.South, []DefensiveThreatFacts{defensiveThreat("raider")}, []SquadDefenderFacts{front, defensiveDefender("b", true)})
	if !ok || len(positions) != 1 || positions[0].Defender != "b" {
		t.Fatal(positions, ok)
	}
}

func TestHoldCompromisedNeedsProof(t *testing.T) {
	firing := cells(9, 23, 8, 23)
	front := defensiveThreat("raider-1")
	if HoldCompromised(firing, domain.North, []DefensiveThreatFacts{front}) {
		t.Fatal("a raider in the trap lane compromises nothing")
	}
	// The sandbags' outer face is still in front of the line.
	front.Position = domain.Known(domain.Cell{X: 9, Z: 21})
	if HoldCompromised(firing, domain.North, []DefensiveThreatFacts{front}) {
		t.Fatal("a raider at the sandbags compromises nothing")
	}
	for name, change := range map[string]func(*DefensiveThreatFacts){
		"on the cover row":   func(f *DefensiveThreatFacts) { f.Position = domain.Known(domain.Cell{X: 30, Z: 22}) },
		"behind the line":    func(f *DefensiveThreatFacts) { f.Position = domain.Known(domain.Cell{X: 9, Z: 40}) },
		"switched to breach": func(f *DefensiveThreatFacts) { f.LordToilClass = domain.Known("LordToil_AssaultColonyBreaching") },
	} {
		f := defensiveThreat("raider-2")
		change(&f)
		if !HoldCompromised(firing, domain.North, []DefensiveThreatFacts{front, f}) {
			t.Fatal(name, "left the hold standing")
		}
		f.Dead = domain.Known(true)
		if HoldCompromised(firing, domain.North, []DefensiveThreatFacts{front, f}) {
			t.Fatal(name, "a dead raider compromised the hold")
		}
	}
	unknown := defensiveThreat("raider-3")
	unknown.Position = domain.Unknown[domain.Cell]()
	unknown.LordToilClass = domain.Unknown[string]()
	if HoldCompromised(firing, domain.North, []DefensiveThreatFacts{unknown}) {
		t.Fatal("unknown evidence abandoned the hold")
	}
	behind := defensiveThreat("raider-4")
	behind.Position = domain.Known(domain.Cell{X: 9, Z: 40})
	if HoldCompromised(firing, "", []DefensiveThreatFacts{behind}) {
		t.Fatal("no corridor direction, no line to be behind")
	}
	// Toward is the edge-to-home direction: facing south the same cell is
	// in front of the line.
	if HoldCompromised(firing, domain.South, []DefensiveThreatFacts{behind}) {
		t.Fatal("south-facing line has the raider in front")
	}
}

// TestExplainDefensivePositionsNamesTheGate pins the refusal text the
// planner logs when it falls back to squad defense (#714).
func TestExplainDefensivePositionsNamesTheGate(t *testing.T) {
	firing := cells(9, 23)
	defenders := []SquadDefenderFacts{defensiveDefender("colonist-a", true)}
	for want, mutate := range map[string]func(*DefensiveThreatFacts){
		"lord unknown":                func(f *DefensiveThreatFacts) { f.LordToilClass = domain.Fact[string]{} },
		"not an edge assault":         func(f *DefensiveThreatFacts) { f.LordJobClass = domain.Known("LordJob_Siege") },
		"engaged, nearest colonist 8": func(f *DefensiveThreatFacts) { f.NearestColonistDistance = domain.Known(8.0) },
		"at or behind the line":       func(f *DefensiveThreatFacts) { f.Position = domain.Known(domain.Cell{X: 9, Z: 30}) },
		"distance unknown":            func(f *DefensiveThreatFacts) { f.NearestColonistDistance = domain.Fact[float64]{} },
	} {
		threat := defensiveThreat("raider-1")
		mutate(&threat)
		if _, got := ExplainDefensivePositions(firing, domain.North, []DefensiveThreatFacts{threat}, defenders); !strings.Contains(got, want) {
			t.Errorf("%s: %q", want, got)
		}
	}
	if _, got := ExplainDefensivePositions(firing, domain.North, []DefensiveThreatFacts{defensiveThreat("raider-1")}, []SquadDefenderFacts{defensiveDefender("colonist-a", false)}); !strings.Contains(got, "no eligible ranged defender") {
		t.Error(got)
	}
	if got, reason := ExplainDefensivePositions(firing, domain.North, []DefensiveThreatFacts{defensiveThreat("raider-1")}, defenders); reason != "" || len(got) != 1 {
		t.Error(got, reason)
	}
}
