package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// grenadeView is holdView with a settled line (every rifleman on its
// firing cell), a holding grenades, and hostiles h1-h3 clustered 10 cells
// south of the line plus a lone h4 nearer.
func grenadeView(weapon string) CombatView {
	view := holdView()
	var threats []SquadThreatFacts
	var positional []DefensiveThreatFacts
	hostiles := map[domain.PawnID]domain.Cell{
		"h1": {X: 9, Z: 13}, "h2": {X: 10, Z: 13}, "h3": {X: 9, Z: 12}, "h4": {X: 5, Z: 16},
	}
	for _, id := range []domain.PawnID{"h1", "h2", "h3", "h4"} {
		s, d := combatRaider(PawnID(id), hostiles[id])
		threats, positional = append(threats, s), append(positional, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: domain.Known(hostiles[id]), Stance: StanceMoving})
	}
	view.Threats, view.Positional = threats, positional
	cells := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}}
	for i := range view.Pawns {
		if c, ok := cells[view.Pawns[i].ID]; ok {
			view.Pawns[i].Cell = domain.Known(c)
		}
		if view.Pawns[i].ID == "a" {
			view.Pawns[i] = withWeapon(view.Pawns[i], weapon)
		}
	}
	return view
}

// {a frag carrier on the line, a three-raider cluster and a lone raider}
// -> one attack_ground at the cluster cell that catches all three.
func TestGrenadeAimsCluster(t *testing.T) {
	orders, _ := decideStop(t, grenadeView("Weapon_GrenadeFrag"), StopEvent{}, CombatMemory{})
	want := []CombatOrder{{Pawn: "a", Kind: OrderAttackGround, Cell: domain.Cell{X: 9, Z: 13}, Reason: ReasonRocketClump}}
	if got := groundOrders(orders); !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", orders)
	}
}

// {the cluster's cells within 2 of a colonist (a brawler out front)} ->
// the throw goes to the lone raider instead; with every hostile near a
// colonist, none.
func TestGrenadeRejectsScatterNearColonist(t *testing.T) {
	carrier := withWeapon(CombatPawnState{ID: "a", Cell: domain.Known(domain.Cell{X: 9, Z: 23})}, "Weapon_GrenadeFrag")
	var hostiles []CombatPawnState
	for id, c := range map[domain.PawnID]domain.Cell{"h1": {X: 9, Z: 13}, "h2": {X: 10, Z: 13}, "h3": {X: 9, Z: 12}, "h4": {X: 5, Z: 16}} {
		hostiles = append(hostiles, CombatPawnState{ID: id, Cell: domain.Known(c)})
	}
	colonists := []domain.Cell{{X: 9, Z: 23}, {X: 10, Z: 11}}
	if got, ok := GrenadeTarget(carrier, hostiles, colonists); !ok || got != (domain.Cell{X: 5, Z: 16}) {
		t.Fatalf("got %v %v, want the lone raider at 5,16", got, ok)
	}
	colonists = append(colonists, domain.Cell{X: 6, Z: 17})
	if got, ok := GrenadeTarget(carrier, hostiles, colonists); ok {
		t.Fatalf("threw at %v with every hostile near a colonist", got)
	}
}

// {an EMP carrier, the unshielded cluster and a shielded lone raider} ->
// the EMP goes to the shielded raider; with no shield or mech, none.
func TestEMPPrefersShielded(t *testing.T) {
	view := grenadeView("Weapon_GrenadeEMP")
	if orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{}); len(groundOrders(orders)) != 0 {
		t.Fatalf("EMP thrown with no shield or mech: %+v", orders)
	}
	for i := range view.Pawns {
		if view.Pawns[i].ID == "h4" {
			view.Pawns[i].Shield = domain.Known(1.0)
		}
	}
	orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{})
	want := []CombatOrder{{Pawn: "a", Kind: OrderAttackGround, Cell: domain.Cell{X: 5, Z: 16}, Reason: ReasonRocketClump}}
	if got := groundOrders(orders); !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", orders)
	}
}
