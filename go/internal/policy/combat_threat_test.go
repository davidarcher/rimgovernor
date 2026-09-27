package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// threatView is holdView with the gunners on their firing cells and five
// raiders, one per threat tier, ids in reverse of their score.
func threatView() CombatView {
	view := holdView()
	view.Threats, view.Positional = nil, nil
	hostiles := []CombatPawnState{
		{ID: "h1", Kind: "Mech_Scyther"},
		{ID: "h2", Kind: "Mech_CentipedeBurner", Weapon: "Gun_InfernoCannon"},
		{ID: "h3", Kind: "Mech_Termite"},
		{ID: "h4", Kind: "Pirate", Weapon: "MeleeWeapon_Knife", Stance: StanceMelee, Target: "c"},
		{ID: "h5", Kind: "Grenadier_Destructive", Weapon: "Weapon_GrenadeFrag"},
	}
	for i, h := range hostiles {
		cell := domain.Cell{X: 8 + int32(i), Z: 10}
		h.Cell = domain.Known(cell)
		s, d := combatRaider(PawnID(h.ID), cell)
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
		view.Pawns = append(view.Pawns, h)
	}
	cells := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}}
	for i := range view.Pawns[:3] {
		view.Pawns[i].Cell, view.Pawns[i].WeaponRange = domain.Known(cells[view.Pawns[i].ID]), 30
	}
	return view
}

// The threat score orders grenadier, melee-on-colonist, termite (a
// sapper), inferno centipede, scyther; a raider sapping by toil ranks
// with the termite, ahead of an ordinary one.
func TestThreatScoreOrder(t *testing.T) {
	view := threatView()
	var got []domain.PawnID
	for _, h := range rankThreats(view) {
		got = append(got, h.ID)
	}
	if want := []domain.PawnID{"h5", "h4", "h3", "h2", "h1"}; !reflect.DeepEqual(got, want) {
		t.Fatalf("ranked %v, want %v", got, want)
	}
	colonists := map[domain.PawnID]bool{"c": true}
	if threatTier(CombatPawnState{Sapper: true}, colonists) != threatSapper || threatTier(CombatPawnState{Kind: "Pirate"}, colonists) != threatOther {
		t.Fatal("sapper or ordinary raider mis-tiered")
	}
}

// Every gunner in place attacks the top-scored hostile, not the lowest id.
func TestDecideCombatFocusesTopThreat(t *testing.T) {
	orders, memory := decideStop(t, threatView(), StopEvent{}, CombatMemory{})
	if len(orders) != 3 {
		t.Fatalf("%+v", orders)
	}
	for _, o := range orders {
		if o.Kind != OrderAttack || o.Target != "h5" {
			t.Fatalf("gunner not on the grenadier: %+v", orders)
		}
	}
	for _, r := range memory.Roles {
		if r.Target != "h5" {
			t.Fatalf("role %+v", r)
		}
	}
}

// The focus holds while its target lives and stays in range, even when a
// higher-scored hostile appears; it moves on only once the target is
// downed, and a gunner the target left the range of retargets alone.
func TestDecideCombatRetargetsOnlyWhenTargetGone(t *testing.T) {
	view := threatView()
	// Start with no grenadier: the melee raider on c is the focus.
	view.Pawns[7].Weapon = "Gun_AssaultRifle"
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	for _, r := range memory.Roles {
		if r.Target != "h4" {
			t.Fatalf("first focus %+v", memory.Roles)
		}
	}
	// A grenade appears: the focus stays on h4.
	view.Tick = 160
	view.Pawns[7].Weapon = "Weapon_GrenadeFrag"
	for i := range view.Pawns[:3] {
		view.Pawns[i].Target, view.Pawns[i].Stance = "h4", StanceIdle
	}
	orders, memory := decideStop(t, view, StopEvent{Kind: "entered_range"}, memory)
	if len(orders) != 0 {
		t.Fatalf("retargeted a live focus: %+v", orders)
	}
	// h4 leaves a's range only (a's range drops to 14): a alone retargets.
	view.Tick = 220
	view.Pawns[0].WeaponRange = 14
	view.Pawns[6].Cell = domain.Known(domain.Cell{X: 30, Z: 20})
	orders, memory = decideStop(t, view, StopEvent{}, memory)
	if want := []CombatOrder{{Pawn: "a", Kind: OrderAttack, Target: "h5", Reason: ReasonFormation}}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	// h4 downed: everyone moves to the grenadier.
	view.Tick = 280
	view.Pawns[6].Downed = true
	view.Pawns[0].Target = "h5"
	orders, _ = decideStop(t, view, StopEvent{Kind: "downed", Pawn: "h4"}, memory)
	if len(orders) != 2 || orders[0].Target != "h5" || orders[1].Target != "h5" {
		t.Fatalf("%+v", orders)
	}
}
