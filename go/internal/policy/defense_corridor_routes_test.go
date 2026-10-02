package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Colonists price their faction's known traps, so the layout's traps are
// off every cheapest colonist route; one in the 1-tile entrance is on every
// route through the hallway, and a corridor walled at the mouth leaves Home no way out once
// the gate is gone.
func TestColonistRoutesAvoidTraps(t *testing.T) {
	r := defenseFixture()
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	s, err := newDefenseSite(r)
	if err != nil {
		t.Fatal(err)
	}
	k, _ := standingCosts(layout, nil)
	for _, start := range []domain.Cell{r.Home, r.Entrances[0]} {
		if !s.colonistRouteAvoidsTraps(k, start) {
			t.Fatal("route from", start, "crosses a trap")
		}
	}
	k.traps[domain.Cell{X: 15, Z: 7}] = true
	lane := map[domain.Cell]bool{}
	for _, c := range layout.TrapLane {
		lane[c] = true
	}
	if s.hallwayAvoidsTraps(k, lane, layout.KillZone(), layout.Entry) {
		t.Fatal("a trap in the entrance read as off the route")
	}
	for i := range r.Cells {
		if r.Cells[i].Cell.X == 2 && r.Cells[i].Cell.Z == 6 {
			r.Cells[i].Passable = domain.Known(false)
		}
	}
	s, _ = newDefenseSite(r)
	k, _ = standingCosts(layout, nil)
	if !s.colonistRouteAvoidsTraps(k, r.Home) {
		t.Fatal("home's way out through the corridor crosses a trap")
	}
	k.closed[layout.Entry] = true
	if s.colonistRouteAvoidsTraps(k, r.Home) {
		t.Fatal("a corridor walled at the mouth still reached the edge")
	}
}
