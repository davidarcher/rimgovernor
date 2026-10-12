package shelter

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The ring's wall stuff is chosen only from stock that covers the shell wall
// budget; a drop short of it leaves the walls on a stuff the colony does not
// hold, and the blueprints never get built (#3018).
func TestBunkWoodCoversShellWallBudget(t *testing.T) {
	if got, need := baselineWood+bunkWood, int(policy.ShellWallBudget)*5; got < need {
		t.Fatalf("baseline and dropped wood = %d, below the %d the shell wall budget needs", got, need)
	}
}

// An empty journal (a restarted service before its first review) must not
// read as restored: shelter/toxic_fallout passed its restore wait on map[]
// and then found the pawns still in the Safe area.
func TestAreasSettledNeedsEvidence(t *testing.T) {
	pawns := []string{"a", "b"}
	none := map[string]domain.Tick{}
	if areasSettled(map[string]string{}, none, 0, false, pawns, false) {
		t.Fatal("empty journal settled the restore")
	}
	if areasSettled(map[string]string{}, none, 0, false, pawns, true) {
		t.Fatal("empty journal settled the shelter")
	}
	sheltered := map[string]string{"a": "Safe", "b": "Safe"}
	at := map[string]domain.Tick{"a": 10, "b": 12}
	if !areasSettled(sheltered, at, 0, false, pawns, true) {
		t.Fatal("sheltered pawns did not settle the shelter")
	}
	if areasSettled(sheltered, at, 0, false, pawns, false) {
		t.Fatal("sheltered pawns settled the restore")
	}
	if areasSettled(sheltered, at, 12, true, pawns, false) {
		t.Fatal("a Safe area delete at b's move tick restored b")
	}
	if !areasSettled(sheltered, at, 20, true, pawns, false) {
		t.Fatal("a later Safe area delete did not restore")
	}
	if !areasSettled(map[string]string{"a": "", "b": ""}, at, 0, false, pawns, false) {
		t.Fatal("cleared pawns did not settle the restore")
	}
}

// A baseline wall on the ring is placed without a wave: the planner matches it
// standing and orders nothing for the cell (#3014).
func TestLineageMissingCountsStandingWalls(t *testing.T) {
	a, b, c := domain.Cell{X: 1, Z: 1}, domain.Cell{X: 2, Z: 1}, domain.Cell{X: 3, Z: 1}
	ring := map[domain.Cell]bool{a: true, b: true, c: true}
	l := lineage{completed: map[domain.Cell]bool{a: true}}
	if got := l.missing(ring, nil); got != 2 {
		t.Fatalf("missing without standing walls = %d, want 2", got)
	}
	if got := l.missing(ring, map[domain.Cell]bool{b: true, c: true}); got != 0 {
		t.Fatalf("missing with the other two standing = %d, want 0", got)
	}
}
