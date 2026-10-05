package policy

import "testing"

// SiteRoom scores the next fitting slots as the initial siting does and keeps
// the best, so it never ranks below the nearest slot it would otherwise take.
func TestSiteRoomKeepsTheBestScoringSlot(t *testing.T) {
	p, s := scoredPlan(t)
	sc := newPlanScorer(p.Zones, p.Reservations, s)
	nearest, ok, err := SiteRoom(p, nil, ModuleThrone, [2]int32{6, 5})
	if !ok || err != nil {
		t.Fatal("nearest slot:", ok, err)
	}
	best, ok, err := SiteRoom(p, &sc, ModuleThrone, [2]int32{6, 5})
	if !ok || err != nil {
		t.Fatal("scored slot:", ok, err)
	}
	if len(best.Rooms) != len(nearest.Rooms) {
		t.Fatal("rooms added", len(best.Rooms)-len(p.Rooms), "want", len(nearest.Rooms)-len(p.Rooms))
	}
	if sc.core(nearest).Better(sc.core(best)) {
		t.Fatal("scored siting ranks below the nearest slot:", sc.core(best), sc.core(nearest))
	}
}
