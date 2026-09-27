package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// {2 digging sappers, 3 riflemen} -> every rifleman moves within 20 cells
// of its nearest digger and attacks it.
func TestDecideCombatSapperInterceptsDiggers(t *testing.T) {
	orders, m := decideStop(t, digging(sapperView()), StopEvent{}, CombatMemory{})
	if !m.Intercept {
		t.Fatalf("%+v", m)
	}
	cells := map[domain.PawnID]domain.Cell{"r1": {X: 9, Z: 5}, "r2": {X: 10, Z: 4}}
	for _, r := range m.Roles {
		if !r.Ranged {
			continue
		}
		d, ok := cells[r.Target]
		if r.Duty != DutyInterceptor || !ok || r.Cell == nil || math.Hypot(float64(r.Cell.X-d.X), float64(r.Cell.Z-d.Z)) > 20.8 {
			t.Fatalf("%+v", r)
		}
	}
	moves := 0
	for _, o := range orders {
		if o.Kind == OrderMove && o.Pawn != "m" {
			moves++
		}
	}
	if moves != 3 || roleCell(t, m, "a").Z >= 30 {
		t.Fatalf("%+v", orders)
	}
}

// {3 sappers, 2 riflemen} -> no intercept.
func TestDecideCombatSapperNoInterceptWhenOutnumbered(t *testing.T) {
	view := withBrawlers(holdView(), combatBrawler("m", 0.5))
	view.Defenders = view.Defenders[1:]
	view.Rooms = []CombatRoom{sapperRoom}
	view = digging(withSappers(view, map[PawnID]domain.Cell{"r1": {X: 9, Z: 5}, "r2": {X: 10, Z: 4}, "r3": {X: 11, Z: 4}}))
	if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Tactic != TacticSapper || m.Intercept {
		t.Fatalf("%+v", m)
	}
}

// {digging mech breachers} -> the gunners stay posted inside the wall.
func TestDecideCombatBreachersNeverIntercepted(t *testing.T) {
	view := digging(sapperView())
	for i := range view.Threats {
		view.Threats[i].Humanlike = domain.Known(false)
	}
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.Intercept || roleCell(t, m, "a") != (domain.Cell{X: 10, Z: 22}) {
		t.Fatalf("%+v", m)
	}
}

// {digging stopped} -> back to the breach posts.
func TestDecideCombatSapperInterceptEndsWhenDiggingStops(t *testing.T) {
	_, m := decideStop(t, digging(sapperView()), StopEvent{}, CombatMemory{})
	view := sapperView()
	_, next := decideStop(t, view, StopEvent{}, m)
	if next.Intercept || roleCell(t, next, "a") != (domain.Cell{X: 10, Z: 22}) {
		t.Fatalf("%+v", next)
	}
}

// digging sets every sapper digging.
func digging(view CombatView) CombatView {
	for i, p := range view.Pawns {
		if p.Sapper {
			view.Pawns[i].Job = "Mine"
		}
	}
	return view
}
