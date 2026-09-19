package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func squadBuilding(id PawnID, lines ...domain.PawnID) SquadThreatFacts {
	t := SquadThreatFacts{ID: id, Dead: domain.Known(false), Building: true, LinesOfFire: map[domain.PawnID]bool{}}
	for _, d := range lines {
		t.LinesOfFire[d] = true
	}
	return t
}

// A building waits for the field to clear, then takes the shooters with a
// line of fire on it in ranged mode and the rest in melee (#327).
func TestSelectSquadDefenseShootsABuildingAlongALineOfFire(t *testing.T) {
	building := squadBuilding("ship", "b", "c")
	// The line only matters for a ranged-equipped defender: "c" holds a
	// club and walks in; "a" has a rifle but no line and walks in too.
	defenders := []SquadDefenderFacts{squadDefender("a", true), squadDefender("b", true), squadDefender("c", false)}
	assignments, ok := SelectSquadDefense([]SquadThreatFacts{building}, defenders)
	if !ok || len(assignments) != 2 {
		t.Fatal(assignments, ok)
	}
	if assignments[0].Defender != "b" || assignments[0].Mode != SquadRanged || assignments[0].Target != "ship" {
		t.Fatal(assignments)
	}
	if assignments[1].Defender != "a" || assignments[1].Mode != SquadMelee {
		t.Fatal(assignments)
	}
	// Two shooters on the line both shoot.
	building = squadBuilding("ship", "a", "b")
	assignments, ok = SelectSquadDefense([]SquadThreatFacts{building}, defenders)
	if !ok || len(assignments) != 2 || assignments[0].Mode != SquadRanged || assignments[1].Mode != SquadRanged {
		t.Fatal(assignments, ok)
	}
	// No line at all: melee as before.
	building = squadBuilding("ship")
	assignments, ok = SelectSquadDefense([]SquadThreatFacts{building}, defenders)
	if !ok || len(assignments) != 2 || assignments[0].Mode != SquadMelee || assignments[1].Mode != SquadMelee {
		t.Fatal(assignments, ok)
	}
	// A live hostile pawn still takes precedence over the building.
	assignments, ok = SelectSquadDefense([]SquadThreatFacts{squadBuilding("ship", "a", "b"), squadThreat("raider", false)}, defenders)
	if !ok || len(assignments) != 2 || assignments[0].Target != "raider" || assignments[1].Target != "raider" {
		t.Fatal(assignments, ok)
	}
	// A destroyed building is no target.
	building = squadBuilding("ship", "a", "b")
	building.Dead = domain.Known(true)
	if assignments, ok := SelectSquadDefense([]SquadThreatFacts{building}, defenders); ok || len(assignments) != 0 {
		t.Fatal(assignments, ok)
	}
}
