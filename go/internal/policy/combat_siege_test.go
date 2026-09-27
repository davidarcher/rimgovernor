package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// siegeView is holdView with r1 and r2 a siege lord in toil, camped at
// z=-20..-19, far south of the line.
func siegeView(toil string) CombatView {
	view := holdView()
	view.Pawns = append(view.Pawns,
		CombatPawnState{ID: "r1", Cell: domain.Known(domain.Cell{X: 9, Z: -20})},
		CombatPawnState{ID: "r2", Cell: domain.Known(domain.Cell{X: 10, Z: -19})})
	for i := range view.Positional {
		view.Positional[i].LordJobClass = domain.Known(siegeLordJob)
		view.Positional[i].LordToilClass = domain.Known(toil)
	}
	return view
}

func attacks(orders []CombatOrder) map[domain.PawnID]domain.PawnID {
	out := map[domain.PawnID]domain.PawnID{}
	for _, o := range orders {
		if o.Kind == OrderAttack {
			out[o.Pawn] = o.Target
		}
	}
	return out
}

// {siege lord travelling, 3 riflemen} -> TacticSiege hold: riflemen to the
// firing line, no attack order.
func TestDecideCombatSiegeHoldsWhileTravelling(t *testing.T) {
	orders, m := decideStop(t, siegeView(siegeTravel), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticSiege || m.SiegeMode != SiegeHold || m.SiegeCamp != 0 {
		t.Fatalf("%+v", m)
	}
	if a := attacks(orders); len(a) != 0 {
		t.Fatalf("sortie before supplies land: %v", a)
	}
	if got := roleCell(t, m, "a"); got != (domain.Cell{X: 9, Z: 23}) {
		t.Fatalf("a at %v", got)
	}
}

// {camp seen at tick 100, stop at 200} -> every rifleman attacks a besieger.
func TestDecideCombatSiegeSortiesOnceCampIsSet(t *testing.T) {
	_, m := decideStop(t, siegeView(siegeTravel), StopEvent{}, CombatMemory{})
	view := siegeView(siegeCampToil)
	_, m = decideStop(t, view, StopEvent{}, m)
	if m.SiegeCamp != 100 {
		t.Fatalf("camp %d", m.SiegeCamp)
	}
	view.Tick = 200
	orders, m := decideStop(t, view, StopEvent{}, m)
	if m.SiegeMode != SiegeSortie {
		t.Fatalf("%+v", m)
	}
	a := attacks(orders)
	for _, id := range []domain.PawnID{"a", "b", "c"} {
		if a[id] != "r1" && a[id] != "r2" {
			t.Errorf("%s attacks %q", id, a[id])
		}
	}
}

// {stop past the window} -> hold again: no attack, riflemen to the line.
func TestDecideCombatSiegeHoldsAfterSortieWindow(t *testing.T) {
	view := siegeView(siegeCampToil)
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	view.Tick += siegeSortieWindow + 1
	orders, m := decideStop(t, view, StopEvent{}, m)
	if m.SiegeMode != SiegeHold {
		t.Fatalf("%+v", m)
	}
	if a := attacks(orders); len(a) != 0 {
		t.Fatalf("sortie into the sandbags: %v", a)
	}
	for _, r := range m.Roles {
		if r.Cell == nil || r.Target != "" {
			t.Errorf("role %+v", r)
		}
	}
}

// {the siege lord assaults} -> the ordinary hold-the-line formation.
func TestDecideCombatSiegeAssaultReformsToHold(t *testing.T) {
	view := siegeView(siegeCampToil)
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	assault := holdView()
	assault.Tick = view.Tick + 10
	_, m = decideStop(t, assault, StopEvent{Kind: StopRaidPhase}, m)
	if m.Tactic != TacticHold {
		t.Fatalf("%+v", m)
	}
}
