package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// corridorOn prices the fixture corridor (trap lane x=9, safe lane x=8,
// rows z=14..19, funnel walls beside them) from a row-by-row spec: t trap,
// f fence, d door, . open.
func corridorOn(t *testing.T, trapLane, safeLane string) corridorCosts {
	t.Helper()
	k := newCorridorCosts()
	for _, c := range cells(10, 14, 11, 14, 7, 15, 10, 15, 7, 16, 10, 16, 7, 17, 10, 17, 7, 18, 10, 18, 7, 19, 10, 19) {
		k.closed[c] = true
	}
	for i, lane := range []string{trapLane, safeLane} {
		for row, ch := range lane {
			c := domain.Cell{X: 9 - int32(i), Z: 14 + int32(row)}
			switch ch {
			case 't':
				k.traps[c] = true
			case 'f':
				k.cost[c] = pathFenceCost
			case 'd':
				k.cost[c], k.full[c] = pathWoodDoorCost, true
			}
		}
	}
	return k
}

// The pre-#619 corridor fenced the safe lane, which priced the trap lane
// as the colonists' cheapest route: vanilla pawns cross their own traps
// for free. The landed corridor keeps the colonists behind the doors from
// Home, from the colony door and from the corridor's own mouth, while a
// door on the last row would let a pawn leaving the colony step onto the
// trap beside it and open the door from there.
func TestColonistRoutesAvoidTraps(t *testing.T) {
	s, err := newDefenseSite(defenseFixture())
	if err != nil {
		t.Fatal(err)
	}
	home, door, exitTrap, exitSafe := s.r.Home, s.r.Entrances[0], domain.Cell{X: 9, Z: 20}, domain.Cell{X: 8, Z: 20}
	all := []domain.Cell{home, door, exitTrap, exitSafe}
	for name, tc := range map[string]struct {
		trapLane, safeLane string
		crossesFrom        []domain.Cell
	}{
		"fenced safe lane":     {".t.t.t", ".f.f.f", all},
		"doored safe lane":     {".tftf.", ".d.d..", nil},
		"trap on the last row": {".tftft", ".d.d..", []domain.Cell{home, door, exitTrap}},
		"no fences between":    {".t.t..", ".d.d..", all},
		"open corridor":        {"......", "......", nil},
	} {
		k := corridorOn(t, tc.trapLane, tc.safeLane)
		for _, start := range all {
			want := true
			for _, c := range tc.crossesFrom {
				want = want && c != start
			}
			if got := s.colonistRouteAvoidsTraps(k, start); got != want {
				t.Errorf("%s from %v: avoids traps %v, want %v", name, start, got, want)
			}
		}
	}
	k := corridorOn(t, ".tftf.", ".d.d..")
	for _, c := range cells(8, 14, 9, 14) {
		k.closed[c] = true
	}
	if s.colonistRouteAvoidsTraps(k, s.r.Home) {
		t.Fatal("a corridor walled at the mouth still reached the edge")
	}
}
