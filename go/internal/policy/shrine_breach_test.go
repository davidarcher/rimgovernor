package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestShrineClearanceTargetsAndHoldReasons(t *testing.T) {
	t.Parallel()
	rows := []AncientShrine{
		{ID: "z-sealed", InHome: true, Sealed: true},
		{ID: "away", Sealed: true},
		{ID: "fogged-open", InHome: true},
		{ID: "guarded", InHome: true, GuardsKnown: true, Guards: []ShrineGuard{{EntityID: "g", Kind: ShrineGuardMechanoid}}},
		{ID: "cleared", InHome: true, GuardsKnown: true, Guards: []ShrineGuard{{EntityID: "g", Kind: ShrineGuardMechanoid, Dead: true}}},
	}
	got := ShrineClearanceTargets(rows)
	if len(got) != 2 || got[0] != "guarded" || got[1] != "z-sealed" {
		t.Fatal(got)
	}
	if r := ShrineHoldReason(rows[3], ShrineReadiness{Ready: true}); r != ShrineHoldGuardsAlive {
		t.Fatal(r)
	}
	if r := ShrineHoldReason(rows[0], ShrineReadiness{Ready: true}); r != ShrineReady {
		t.Fatal(r)
	}
	if r := ShrineHoldReason(rows[0], ShrineReadiness{Reason: ShrineHoldNoTraps}); r != ShrineHoldNoTraps {
		t.Fatal(r)
	}
}

func TestShrineBreachDraftsLeaveOneColonistFree(t *testing.T) {
	t.Parallel()
	squad := []domain.PawnID{"a", "b", "c"}
	if got := ShrineBreachDrafts(squad, 4); len(got) != 3 {
		t.Fatal(got)
	}
	if got := ShrineBreachDrafts(squad, 3); len(got) != 2 || got[1] != "b" {
		t.Fatal(got)
	}
	if got := ShrineBreachDrafts([]domain.PawnID{"a"}, 1); len(got) != 1 {
		t.Fatal(got)
	}
}

func TestShrineBreachPositionsStandBehindTheTrapLine(t *testing.T) {
	t.Parallel()
	wall := ShrineBreachWall{EntityID: "w", Cell: domain.Cell{X: 30, Z: 35}, Outside: domain.Cell{X: 29, Z: 35}}
	traps := []domain.Cell{{X: 27, Z: 35}, {X: 26, Z: 35}, {X: 21, Z: 36}}
	standing := []domain.Cell{
		{X: 28, Z: 35}, // inside the trap line
		{X: 21, Z: 36}, // a trap
		{X: 21, Z: 35}, // ideal
		{X: 21, Z: 35}, // duplicate
		{X: 22, Z: 35},
		{X: 20, Z: 34},
		{X: 10, Z: 35}, // beyond the trap radius
		{X: 35, Z: 35}, // behind the wall
	}
	got := ShrineBreachPositions(wall, []domain.PawnID{"a", "b", "c", "d"}, standing, traps)
	if len(got) != 3 || got["a"] != (domain.Cell{X: 21, Z: 35}) || got["b"] != (domain.Cell{X: 22, Z: 35}) || got["c"] != (domain.Cell{X: 20, Z: 34}) {
		t.Fatal(got)
	}
	if _, ok := got["d"]; ok {
		t.Fatal("no cell left for d", got)
	}
	// A diagonal or coincident outside cell yields no positions.
	if got := ShrineBreachPositions(ShrineBreachWall{Cell: domain.Cell{X: 1, Z: 1}, Outside: domain.Cell{X: 2, Z: 2}}, []domain.PawnID{"a"}, standing, nil); len(got) != 0 {
		t.Fatal(got)
	}
}
