package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func defensiveThreat(id string) DefensiveThreatFacts {
	return DefensiveThreatFacts{ID: PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false), Humanlike: domain.Known(true),
		LordJobClass: domain.Known("LordJob_AssaultColony"), LordToilClass: domain.Known("LordToil_AssaultColony"), NearestColonistDistance: domain.Known(40.0)}
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
	got, ok := SelectDefensivePositions(firing, threats, defenders)
	want := []DefensivePosition{{Defender: "colonist-a", Cell: domain.Cell{X: 9, Z: 23}, Target: "raider-1"}, {Defender: "colonist-b", Cell: domain.Cell{X: 8, Z: 23}, Target: "raider-1"}}
	if !ok || !reflect.DeepEqual(got, want) {
		t.Fatalf("%v %+v", ok, got)
	}
	threats[1].Dead = domain.Known(true)
	if got, ok = SelectDefensivePositions(firing, threats, defenders); !ok || got[0].Target != "raider-2" {
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
		"animal":           func(f *DefensiveThreatFacts) { f.Humanlike = domain.Known(false) },
		"unknown dead":     func(f *DefensiveThreatFacts) { f.Dead = domain.Unknown[bool]() },
	} {
		f := defensiveThreat("raider-1")
		threat(&f)
		if got, ok := SelectDefensivePositions(firing, []DefensiveThreatFacts{defensiveThreat("raider-2"), f}, defenders); ok {
			t.Fatal(name, "positioned", got)
		}
	}
	if _, ok := SelectDefensivePositions(nil, []DefensiveThreatFacts{defensiveThreat("raider-1")}, defenders); ok {
		t.Fatal("positioned without a layout")
	}
	if _, ok := SelectDefensivePositions(firing, []DefensiveThreatFacts{defensiveThreat("raider-1")}, []SquadDefenderFacts{defensiveDefender("colonist-a", false)}); ok {
		t.Fatal("melee defender positioned")
	}
	hurt := defensiveDefender("colonist-a", true)
	hurt.HealthFraction = domain.Known(0.4)
	if _, ok := SelectDefensivePositions(firing, []DefensiveThreatFacts{defensiveThreat("raider-1")}, []SquadDefenderFacts{hurt}); ok {
		t.Fatal("wounded defender positioned")
	}
	all := []DefensiveThreatFacts{defensiveThreat("raider-1")}
	all[0].Downed = domain.Known(true)
	if _, ok := SelectDefensivePositions(firing, all, defenders); ok {
		t.Fatal("positioned against no live hostile")
	}
}

func TestSelectDefensivePositionsSeatsShootersFirst(t *testing.T) {
	front := defensiveDefender("a", true)
	front.FrontLine = true
	positions, ok := SelectDefensivePositions([]domain.Cell{{X: 1, Z: 1}}, []DefensiveThreatFacts{defensiveThreat("raider")}, []SquadDefenderFacts{front, defensiveDefender("b", true)})
	if !ok || len(positions) != 1 || positions[0].Defender != "b" {
		t.Fatal(positions, ok)
	}
}
